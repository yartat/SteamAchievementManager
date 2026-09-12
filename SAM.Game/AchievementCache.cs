/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SAM.Game
{
    /// <summary>
    /// One cached achievement: the definition out of the schema, plus the state
    /// Steam last reported for it.
    /// </summary>
    internal sealed class CachedAchievement
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string IconNormal { get; set; }
        public string IconLocked { get; set; }
        public int Permission { get; set; }
        public bool IsAchieved { get; set; }
        public DateTime? UnlockTime { get; set; }
    }

    /// <summary>
    /// SQLite-backed cache of a game's achievement list, so the window can be
    /// painted before Steam answers <c>RequestUserStats</c> instead of after.
    /// </summary>
    /// <remarks>
    /// This is the same database file the picker keeps its game list in, in a
    /// table of its own. Both processes can hold it open because the schema is
    /// created with WAL, which allows concurrent readers alongside one writer;
    /// <c>Default Timeout</c> covers the moment the two overlap.
    /// <para>
    /// Rows are keyed by account as well as app: achievement state belongs to
    /// whoever was logged in, and a machine can have more than one Steam
    /// account. Language is part of the lookup too, because the cached text is
    /// the localized text — a language switch has to miss rather than show the
    /// previous one.
    /// </para>
    /// </remarks>
    internal sealed class AchievementCache : IDisposable
    {
        private readonly SqliteConnection _Connection;

        private AchievementCache(SqliteConnection connection)
        {
            this._Connection = connection;
        }

        public static AchievementCache Open(string databasePath)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrEmpty(directory) == false)
            {
                Directory.CreateDirectory(directory);
            }

            SqliteConnection connection = new(new SqliteConnectionStringBuilder()
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // The picker may be writing its own table at the same moment.
                DefaultTimeout = 5,
            }.ToString());
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS achievements (
                        account_id   INTEGER NOT NULL,
                        app_id       INTEGER NOT NULL,
                        id           TEXT NOT NULL,
                        language     TEXT NOT NULL,
                        sort_order   INTEGER NOT NULL DEFAULT 0,
                        name         TEXT,
                        description  TEXT,
                        icon_normal  TEXT,
                        icon_locked  TEXT,
                        permission   INTEGER NOT NULL DEFAULT 0,
                        is_achieved  INTEGER NOT NULL DEFAULT 0,
                        unlock_time  INTEGER,
                        updated_utc  INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (account_id, app_id, id)
                    );
                    """;
                command.ExecuteNonQuery();
            }

            return new(connection);
        }

        /// <summary>
        /// The achievements last written for this account, app and language, in
        /// the order the schema listed them. Empty means a cold start.
        /// </summary>
        public List<CachedAchievement> Load(long accountId, long appId, string language)
        {
            List<CachedAchievement> result = new();

            using var command = this._Connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, description, icon_normal, icon_locked,
                       permission, is_achieved, unlock_time
                FROM achievements
                WHERE account_id = $account AND app_id = $app AND language = $language
                ORDER BY sort_order
                """;
            command.Parameters.AddWithValue("$account", accountId);
            command.Parameters.AddWithValue("$app", appId);
            command.Parameters.AddWithValue("$language", language ?? "");

            using var reader = command.ExecuteReader();
            while (reader.Read() == true)
            {
                result.Add(new()
                {
                    Id = reader.GetString(0),
                    Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IconNormal = reader.IsDBNull(3) ? null : reader.GetString(3),
                    IconLocked = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Permission = reader.GetInt32(5),
                    IsAchieved = reader.GetInt32(6) != 0,
                    UnlockTime = reader.IsDBNull(7)
                        ? null
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)).LocalDateTime,
                });
            }
            return result;
        }

        /// <summary>
        /// Replaces everything held for this account and app with
        /// <paramref name="achievements"/>. One transaction, so a crash mid-write
        /// cannot leave a half-written list behind. Rows in a previously cached
        /// language go with it — only one language is kept per game.
        /// </summary>
        public void Sync(
            long accountId,
            long appId,
            string language,
            IReadOnlyList<CachedAchievement> achievements)
        {
            using var transaction = this._Connection.BeginTransaction();

            using (var delete = this._Connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM achievements WHERE account_id = $account AND app_id = $app";
                delete.Parameters.AddWithValue("$account", accountId);
                delete.Parameters.AddWithValue("$app", appId);
                delete.ExecuteNonQuery();
            }

            using (var insert = this._Connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO achievements (account_id, app_id, id, language, sort_order, name,
                                              description, icon_normal, icon_locked,
                                              permission, is_achieved, unlock_time, updated_utc)
                    VALUES ($account, $app, $id, $language, $order, $name, $description, $normal,
                            $locked, $permission, $achieved, $unlocked, $updated)
                    """;

                Dictionary<string, SqliteParameter> parameters = new();
                foreach (var name in new[]
                {
                    "$account", "$app", "$id", "$language", "$order", "$name", "$description",
                    "$normal", "$locked", "$permission", "$achieved", "$unlocked",
                    "$updated",
                })
                {
                    var parameter = insert.CreateParameter();
                    parameter.ParameterName = name;
                    insert.Parameters.Add(parameter);
                    parameters[name] = parameter;
                }

                parameters["$account"].Value = accountId;
                parameters["$app"].Value = appId;
                parameters["$language"].Value = language ?? "";
                parameters["$updated"].Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                for (int index = 0; index < achievements.Count; index++)
                {
                    var achievement = achievements[index];
                    parameters["$id"].Value = achievement.Id;
                    parameters["$order"].Value = index;
                    parameters["$name"].Value = (object)achievement.Name ?? DBNull.Value;
                    parameters["$description"].Value = (object)achievement.Description ?? DBNull.Value;
                    parameters["$normal"].Value = (object)achievement.IconNormal ?? DBNull.Value;
                    parameters["$locked"].Value = (object)achievement.IconLocked ?? DBNull.Value;
                    parameters["$permission"].Value = achievement.Permission;
                    parameters["$achieved"].Value = achievement.IsAchieved ? 1 : 0;
                    parameters["$unlocked"].Value = achievement.UnlockTime.HasValue == false
                        ? DBNull.Value
                        : new DateTimeOffset(achievement.UnlockTime.Value.ToUniversalTime(), TimeSpan.Zero)
                            .ToUnixTimeSeconds();
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        public void Dispose()
        {
            this._Connection?.Close();
            this._Connection?.Dispose();
        }
    }
}
