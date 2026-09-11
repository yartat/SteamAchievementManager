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
using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SAM.Picker
{
    /// <summary>
    /// A row of the cached library, as stored in SQLite.
    /// </summary>
    internal sealed class CachedGame
    {
        public uint Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string ImageUrl { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int? RatingPercent { get; set; }
        public int? RatingScore { get; set; }
        public int PlaytimeMinutes { get; set; }
        public DateTime? LastPlayed { get; set; }
        public int AchievementsTotal { get; set; } = -1;
        public int AchievementsEarned { get; set; } = -1;
        public bool HasStats { get; set; }
    }

    /// <summary>
    /// SQLite-backed cache of the owned game list and its per-game stats, so
    /// the picker can show a populated window immediately instead of waiting on
    /// a network round trip and a few hundred Steam calls.
    /// </summary>
    /// <remarks>
    /// Deliberately raw ADO.NET rather than EF Core: this is two tables and a
    /// handful of statements, and <c>SAM.API</c>'s no-dependency character is
    /// worth preserving in the apps too.
    /// </remarks>
    internal sealed class GameCache : IDisposable
    {
        private readonly SqliteConnection _Connection;

        public string Path { get; }

        private GameCache(SqliteConnection connection, string path)
        {
            this._Connection = connection;
            this.Path = path;
        }

        public static GameCache Open(string databasePath)
        {
            var directory = System.IO.Path.GetDirectoryName(databasePath);
            if (string.IsNullOrEmpty(directory) == false)
            {
                Directory.CreateDirectory(directory);
            }

            SqliteConnection connection = new(new SqliteConnectionStringBuilder()
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS games (
                        id                 INTEGER PRIMARY KEY,
                        type               TEXT,
                        name               TEXT,
                        image_url          TEXT,
                        release_date       INTEGER,
                        rating_percent     INTEGER,
                        rating_score       INTEGER,
                        playtime_minutes   INTEGER NOT NULL DEFAULT 0,
                        last_played        INTEGER,
                        ach_total          INTEGER NOT NULL DEFAULT -1,
                        ach_earned         INTEGER NOT NULL DEFAULT -1,
                        has_stats          INTEGER NOT NULL DEFAULT 0,
                        updated_utc        INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                command.ExecuteNonQuery();
            }

            return new(connection, databasePath);
        }

        public List<CachedGame> LoadAll()
        {
            List<CachedGame> result = new();
            using var command = this._Connection.CreateCommand();
            command.CommandText = """
                SELECT id, type, name, image_url, release_date, rating_percent, rating_score,
                       playtime_minutes, last_played, ach_total, ach_earned, has_stats
                FROM games
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read() == true)
            {
                result.Add(new()
                {
                    Id = (uint)reader.GetInt64(0),
                    Type = reader.IsDBNull(1) ? "normal" : reader.GetString(1),
                    Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ImageUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ReleaseDate = ReadDate(reader, 4),
                    RatingPercent = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    RatingScore = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    PlaytimeMinutes = reader.GetInt32(7),
                    LastPlayed = ReadDate(reader, 8),
                    AchievementsTotal = reader.GetInt32(9),
                    AchievementsEarned = reader.GetInt32(10),
                    HasStats = reader.GetInt32(11) != 0,
                });
            }
            return result;
        }

        private static DateTime? ReadDate(IDataRecord reader, int ordinal)
        {
            return reader.IsDBNull(ordinal)
                ? null
                : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(ordinal)).LocalDateTime;
        }

        private static object WriteDate(DateTime? value)
        {
            return value.HasValue == false
                ? DBNull.Value
                : new DateTimeOffset(value.Value.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds();
        }

        /// <summary>
        /// Makes the table match <paramref name="games"/> exactly: rows for
        /// games no longer owned are deleted, new ones inserted, existing ones
        /// updated. One transaction, so a crash mid-sync cannot leave a
        /// half-written library behind.
        /// </summary>
        public void Sync(IReadOnlyCollection<CachedGame> games)
        {
            using var transaction = this._Connection.BeginTransaction();

            using (var command = this._Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS keep (id INTEGER PRIMARY KEY); DELETE FROM keep;";
                command.ExecuteNonQuery();
            }

            using (var keep = this._Connection.CreateCommand())
            {
                keep.Transaction = transaction;
                keep.CommandText = "INSERT OR IGNORE INTO keep(id) VALUES($id)";
                var id = keep.CreateParameter();
                id.ParameterName = "$id";
                keep.Parameters.Add(id);

                foreach (var game in games)
                {
                    id.Value = (long)game.Id;
                    keep.ExecuteNonQuery();
                }
            }

            using (var upsert = this._Connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO games (id, type, name, image_url, release_date, rating_percent,
                                       rating_score, playtime_minutes, last_played, ach_total,
                                       ach_earned, has_stats, updated_utc)
                    VALUES ($id, $type, $name, $url, $released, $percent, $score, $playtime,
                            $lastPlayed, $total, $earned, $hasStats, $updated)
                    ON CONFLICT(id) DO UPDATE SET
                        type = excluded.type,
                        name = excluded.name,
                        image_url = excluded.image_url,
                        release_date = excluded.release_date,
                        rating_percent = excluded.rating_percent,
                        rating_score = excluded.rating_score,
                        playtime_minutes = excluded.playtime_minutes,
                        last_played = excluded.last_played,
                        ach_total = excluded.ach_total,
                        ach_earned = excluded.ach_earned,
                        has_stats = excluded.has_stats,
                        updated_utc = excluded.updated_utc
                    """;

                var parameters = new Dictionary<string, SqliteParameter>();
                foreach (var name in new[]
                {
                    "$id", "$type", "$name", "$url", "$released", "$percent", "$score",
                    "$playtime", "$lastPlayed", "$total", "$earned", "$hasStats", "$updated",
                })
                {
                    var parameter = upsert.CreateParameter();
                    parameter.ParameterName = name;
                    upsert.Parameters.Add(parameter);
                    parameters[name] = parameter;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var game in games)
                {
                    parameters["$id"].Value = (long)game.Id;
                    parameters["$type"].Value = (object)game.Type ?? DBNull.Value;
                    parameters["$name"].Value = (object)game.Name ?? DBNull.Value;
                    parameters["$url"].Value = (object)game.ImageUrl ?? DBNull.Value;
                    parameters["$released"].Value = WriteDate(game.ReleaseDate);
                    parameters["$percent"].Value = (object)game.RatingPercent ?? DBNull.Value;
                    parameters["$score"].Value = (object)game.RatingScore ?? DBNull.Value;
                    parameters["$playtime"].Value = game.PlaytimeMinutes;
                    parameters["$lastPlayed"].Value = WriteDate(game.LastPlayed);
                    parameters["$total"].Value = game.AchievementsTotal;
                    parameters["$earned"].Value = game.AchievementsEarned;
                    parameters["$hasStats"].Value = game.HasStats ? 1 : 0;
                    parameters["$updated"].Value = now;
                    upsert.ExecuteNonQuery();
                }
            }

            using (var prune = this._Connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = "DELETE FROM games WHERE id NOT IN (SELECT id FROM keep)";
                prune.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void Dispose()
        {
            this._Connection?.Close();
            this._Connection?.Dispose();
            // Without this the WAL sidecar files can stay locked, which breaks
            // moving the database to a new location in the same session.
            SqliteConnection.ClearAllPools();
        }

        /// <summary>
        /// Closes the database, moves it (with its WAL sidecars) to
        /// <paramref name="destination"/>, and reopens it there.
        /// </summary>
        public static GameCache MoveTo(GameCache cache, string destination)
        {
            var source = cache.Path;
            cache.Dispose();

            if (string.Equals(
                    System.IO.Path.GetFullPath(source),
                    System.IO.Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase) == false)
            {
                var directory = System.IO.Path.GetDirectoryName(destination);
                if (string.IsNullOrEmpty(directory) == false)
                {
                    Directory.CreateDirectory(directory);
                }

                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var from = source + suffix;
                    var to = destination + suffix;
                    if (File.Exists(from) == false)
                    {
                        continue;
                    }
                    File.Move(from, to, true);
                }
            }

            return Open(destination);
        }
    }
}
