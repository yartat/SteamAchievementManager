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
using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers
{
    public class SteamUser012 : NativeWrapper<ISteamUser012>
    {
        #region IsLoggedIn
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool NativeLoggedOn(IntPtr self);

        public bool IsLoggedIn()
        {
            return this.Call<bool, NativeLoggedOn>(this.Functions.LoggedOn, this.ObjectAddress);
        }
        #endregion

        #region GetSteamID

        // ISteamUser::GetSteamID() returns CSteamID -- a 64-bit POD -- *by value*,
        // and the two platform ABIs disagree about how that happens:
        //
        //   MSVC x86/x64 : through a hidden pointer passed as an extra argument.
        //   System V AMD64 (Linux, macOS) : directly in RAX, no extra argument.
        //
        // Getting it wrong does not fail loudly; the indirect form on Linux
        // reads a slot nobody wrote and quietly returns 0, which then poisons
        // everything keyed off the account id. Verified both ways against a
        // live client on Windows and on Debian 13.
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void NativeGetSteamIdIndirect(IntPtr self, out ulong steamId);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate ulong NativeGetSteamIdDirect(IntPtr self);

        // Cached here rather than through GetFunction: the base class keys its
        // delegate cache on the function pointer alone, so two delegate types
        // sharing one vtable slot would collide.
        private NativeGetSteamIdIndirect _GetSteamIdIndirect;
        private NativeGetSteamIdDirect _GetSteamIdDirect;

        public ulong GetSteamId()
        {
            if (OperatingSystem.IsWindows())
            {
                this._GetSteamIdIndirect ??=
                    Marshal.GetDelegateForFunctionPointer<NativeGetSteamIdIndirect>(this.Functions.GetSteamID);
                this._GetSteamIdIndirect(this.ObjectAddress, out var steamId);
                return steamId;
            }

            this._GetSteamIdDirect ??=
                Marshal.GetDelegateForFunctionPointer<NativeGetSteamIdDirect>(this.Functions.GetSteamID);
            return this._GetSteamIdDirect(this.ObjectAddress);
        }

        #endregion
    }
}
