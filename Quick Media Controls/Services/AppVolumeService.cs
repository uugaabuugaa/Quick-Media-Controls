using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Quick_Media_Controls.Services
{
    /// <summary>
    /// Reads and changes the per-app volume and mute state that the Windows Volume Mixer shows.
    /// Everything runs on the caller's thread (the UI thread). Matching audio sessions are cached
    /// for a couple of seconds so dragging a slider does not re-enumerate Windows audio on every tick.
    /// </summary>
    public sealed class AppVolumeService : IDisposable
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

        // Some browsers report a media session id that is not their process name.
        private static readonly ReadOnlyDictionary<string, string> AppIdAliases =
            new(new Dictionary<string, string>
            {
                ["308046b0af4a39cb"] = "firefox",
                ["firefox"] = "firefox",
            });

        private readonly List<AudioSessionControl> _sessions = new();
        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private string? _cachedAppId;
        private DateTime _cachedAtUtc = DateTime.MinValue;
        private bool _disposed;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exePath, ref int size);

        private const uint ProcessQueryLimitedInformation = 0x1000;

        /// <summary>
        /// Gets the Volume Mixer state for the app behind a media session.
        /// Returns false when no matching audio session exists (for example, nothing has played yet).
        /// </summary>
        public bool TryGetState(string? appId, out float volume, out bool muted)
        {
            volume = 0f;
            muted = false;

            EnsureSessions(appId, forceRefresh: false);
            if (_sessions.Count == 0) return false;

            try
            {
                float maxVolume = 0f;
                bool allMuted = true;

                foreach (var session in _sessions)
                {
                    var simple = session.SimpleAudioVolume;
                    maxVolume = Math.Max(maxVolume, simple.Volume);
                    allMuted &= simple.Mute;
                }

                volume = maxVolume;
                muted = allMuted;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read app volume: {ex.Message}");
                InvalidateCache();
                return false;
            }
        }

        /// <summary>Sets the Volume Mixer level (0.0 - 1.0) for the app behind a media session.</summary>
        public bool TrySetVolume(string? appId, float volume)
        {
            volume = Math.Clamp(volume, 0f, 1f);
            return ApplyToSessions(appId, simple => simple.Volume = volume);
        }

        /// <summary>Sets the Volume Mixer mute flag for the app behind a media session.</summary>
        public bool TrySetMute(string? appId, bool mute)
        {
            return ApplyToSessions(appId, simple => simple.Mute = mute);
        }

        private bool ApplyToSessions(string? appId, Action<SimpleAudioVolume> change)
        {
            EnsureSessions(appId, forceRefresh: false);
            if (_sessions.Count == 0) return false;

            bool anySucceeded = false;
            foreach (var session in _sessions)
            {
                try
                {
                    change(session.SimpleAudioVolume);
                    anySucceeded = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to change app volume: {ex.Message}");
                }
            }

            if (!anySucceeded) InvalidateCache();
            return anySucceeded;
        }

        private void InvalidateCache()
        {
            _cachedAtUtc = DateTime.MinValue;
        }

        private void EnsureSessions(string? appId, bool forceRefresh)
        {
            if (_disposed) return;

            bool isStale = forceRefresh
                || !string.Equals(_cachedAppId, appId, StringComparison.Ordinal)
                || DateTime.UtcNow - _cachedAtUtc > CacheLifetime;
            if (!isStale) return;

            ReleaseSessions();
            _cachedAppId = appId;
            _cachedAtUtc = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(appId)) return;

            try
            {
                _enumerator = new MMDeviceEnumerator();
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                var manager = _device.AudioSessionManager;
                manager.RefreshSessions();

                var allSessions = manager.Sessions;
                int count = allSessions.Count;

                for (int i = 0; i < count; i++)
                {
                    AudioSessionControl? session = null;
                    bool keep = false;

                    try
                    {
                        session = allSessions[i];
                        if (session.IsSystemSoundsSession) continue;

                        int processId = (int)session.GetProcessID;
                        if (processId == 0) continue;

                        if (IsSessionOfApp(appId, processId))
                        {
                            _sessions.Add(session);
                            keep = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Skipping audio session: {ex.Message}");
                    }
                    finally
                    {
                        if (!keep) session?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to enumerate audio sessions: {ex.Message}");
                ReleaseSessions();
            }
        }

        private static bool IsSessionOfApp(string appId, int processId)
        {
            string processName;
            try
            {
                using var process = Process.GetProcessById(processId);
                processName = process.ProcessName;
            }
            catch
            {
                return false;
            }

            return AppIdMatchesProcess(appId, processName, TryGetProcessPath(processId));
        }

        /// <summary>
        /// Matches a media session's SourceAppUserModelId (for example "Spotify.exe", "Chrome",
        /// or "SpotifyAB.SpotifyMusic_xxxx!Spotify") to an audio session's process.
        /// </summary>
        private static bool AppIdMatchesProcess(string appId, string processName, string? exePath)
        {
            string id = appId.Trim().ToLowerInvariant();
            string process = processName.ToLowerInvariant();

            if (id.EndsWith(".exe", StringComparison.Ordinal)) id = id[..^4];
            if (id == process) return true;

            if (AppIdAliases.TryGetValue(id, out var alias) && alias == process) return true;

            int bang = id.IndexOf('!');
            int underscore = id.IndexOf('_');

            // Packaged apps: "Family_hash!AppName"
            string appPart = bang >= 0 ? id[(bang + 1)..] : id;
            if (appPart == process) return true;

            if (bang >= 0 && underscore > 0 && underscore < bang && !string.IsNullOrEmpty(exePath))
            {
                string family = id[..underscore];
                if (exePath.ToLowerInvariant().Contains("\\" + family + "_", StringComparison.Ordinal)) return true;
            }

            return process.Length >= 5 && id.Contains(process, StringComparison.Ordinal);
        }

        private static string? TryGetProcessPath(int processId)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
                if (handle == IntPtr.Zero) return null;

                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }

        private void ReleaseSessions()
        {
            foreach (var session in _sessions)
            {
                try { session.Dispose(); } catch { /* already released */ }
            }
            _sessions.Clear();

            try { _device?.Dispose(); } catch { /* already released */ }
            try { _enumerator?.Dispose(); } catch { /* already released */ }
            _device = null;
            _enumerator = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseSessions();
        }
    }
}
