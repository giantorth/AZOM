using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// On-disk + in-memory cache of parsed dashboard profiles, keyed by MD5 hash
    /// (from session 0x09 <c>enabledDashboards[].Hash</c>).
    ///
    /// Lifecycle:
    ///   1. <see cref="LoadFromDisk"/> on plugin init — reads cached .mzdash files.
    ///   2. <see cref="Ingest"/> when a dashboard is uploaded or downloaded — saves
    ///      raw mzdash content to disk, parses it, and stores the profile in memory.
    ///   3. <see cref="TryGetByName"/> during dashboard switch — looks up the
    ///      profile by dashboard name.
    /// </summary>
    public class DashboardCache
    {
        private readonly string _cacheDir;
        private readonly DashboardProfileStore _store;
        private readonly object _lock = new object();

        // hash → parsed profile
        private readonly Dictionary<string, MultiStreamProfile> _byHash =
            new Dictionary<string, MultiStreamProfile>(StringComparer.OrdinalIgnoreCase);

        // name → hash (populated from session 0x09 state)
        private readonly Dictionary<string, string> _nameToHash =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // hash → raw mzdash content (for upload to wheel)
        private readonly Dictionary<string, byte[]> _rawContent =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public int WheelCacheCount { get { lock (_lock) return _byHash.Count; } }
        public int FolderProfileCount { get { lock (_lock) return _folderProfiles.Count; } }

        // --- User folder library (fallback after wheel cache) ---
        private readonly Dictionary<string, MultiStreamProfile> _folderProfiles =
            new Dictionary<string, MultiStreamProfile>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _folderRawContent =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _folderFilePaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _folderHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public DashboardCache(string cacheDir, DashboardProfileStore store)
        {
            _cacheDir = cacheDir;
            _store = store;
        }

        /// <summary>Number of cached profiles.</summary>
        public int Count { get { lock (_lock) return _byHash.Count; } }

        /// <summary>
        /// Scan the cache directory for previously-saved .mzdash files and parse them.
        /// File names are <c>{hash}.mzdash</c>.
        /// </summary>
        public void LoadFromDisk()
        {
            if (!Directory.Exists(_cacheDir))
            {
                try { Directory.CreateDirectory(_cacheDir); }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] DashboardCache: cannot create {_cacheDir}: {ex.Message}");
                    return;
                }
            }

            lock (_lock)
            {
                foreach (var file in Directory.GetFiles(_cacheDir, "*.mzdash"))
                {
                    try
                    {
                        string hash = Path.GetFileNameWithoutExtension(file);
                        if (_byHash.ContainsKey(hash)) continue;

                        string content = File.ReadAllText(file);
                        string name = hash;
                        if (content.StartsWith("//name:"))
                        {
                            int nl = content.IndexOf('\n');
                            if (nl > 0)
                            {
                                name = content.Substring(7, nl - 7).Trim();
                                content = content.Substring(nl + 1);
                            }
                        }

                        var profile = _store.ParseMzdashContent(name, content);
                        if (profile != null)
                        {
                            _byHash[hash] = profile;
                            _nameToHash[name] = hash;
                            _rawContent[hash] = System.Text.Encoding.UTF8.GetBytes(content);
                            MozaLog.Debug($"[AZOM] DashboardCache: loaded '{name}' from disk (hash={hash.Substring(0, 8)}...)");
                        }
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[AZOM] DashboardCache: failed to load {file}: {ex.Message}");
                    }
                }

                MozaLog.Debug($"[AZOM] DashboardCache: {_byHash.Count} profiles loaded from disk");
            }
        }

        /// <summary>
        /// Update the name → hash mapping from session 0x09 wheel state.
        /// Call this every time a new <see cref="WheelDashboardState"/> arrives.
        /// Returns the list of hashes that are NOT in the cache (need download).
        /// </summary>
        public List<string> UpdateFromWheelState(WheelDashboardState state)
        {
            var missing = new List<string>();
            lock (_lock)
            {
                _nameToHash.Clear();

                if (state.EnabledDashboards == null) return missing;

                foreach (var dash in state.EnabledDashboards)
                {
                    if (string.IsNullOrEmpty(dash.Title) || string.IsNullOrEmpty(dash.Hash))
                        continue;

                    _nameToHash[dash.Title] = dash.Hash;

                    if (!_byHash.ContainsKey(dash.Hash))
                        missing.Add(dash.Hash);
                }
            }

            if (missing.Count > 0)
                MozaLog.Debug($"[AZOM] DashboardCache: {missing.Count} dashboards not cached, need download");

            return missing;
        }

        /// <summary>
        /// Ingest a raw mzdash file — parse it, cache in memory, and persist to disk.
        /// </summary>
        public bool Ingest(string hash, string dashboardName, string mzdashContent)
        {
            try
            {
                var profile = _store.ParseMzdashContent(dashboardName, mzdashContent);
                if (profile == null)
                {
                    MozaLog.Warn($"[AZOM] DashboardCache: failed to parse mzdash for '{dashboardName}'");
                    return false;
                }

                lock (_lock)
                {
                    _byHash[hash] = profile;
                    _nameToHash[dashboardName] = hash;
                    _rawContent[hash] = System.Text.Encoding.UTF8.GetBytes(mzdashContent);
                }

                // Persist to disk (outside lock — file I/O can be slow)
                try
                {
                    if (!Directory.Exists(_cacheDir))
                        Directory.CreateDirectory(_cacheDir);

                    string filePath = Path.Combine(_cacheDir, $"{hash}.mzdash");
                    File.WriteAllText(filePath, $"//name:{dashboardName}\n{mzdashContent}");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] DashboardCache: disk write failed for '{dashboardName}': {ex.Message}");
                }

                MozaLog.Debug($"[AZOM] DashboardCache: ingested '{dashboardName}' (hash={hash.Substring(0, Math.Min(8, hash.Length))}...)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] DashboardCache: ingest failed for '{dashboardName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Ingest raw mzdash bytes (e.g. from download or embedded resource).
        /// </summary>
        public bool Ingest(string hash, string dashboardName, byte[] mzdashBytes)
        {
            string content = System.Text.Encoding.UTF8.GetString(mzdashBytes);
            return Ingest(hash, dashboardName, content);
        }

        /// <summary>
        /// Look up a cached profile by dashboard name.
        /// Returns null if no profile is cached for this name.
        /// </summary>
        public MultiStreamProfile? TryGetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                if (_nameToHash.TryGetValue(name, out var hash))
                {
                    if (_byHash.TryGetValue(hash, out var profile))
                        return profile;
                }
                if (TryGetNormalized(_folderProfiles, name, out var folderProfile))
                    return folderProfile;
            }
            return null;
        }

        /// <summary>
        /// Look up a cached profile by hash.
        /// </summary>
        public MultiStreamProfile? TryGetByHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            lock (_lock)
            {
                _byHash.TryGetValue(hash, out var profile);
                return profile;
            }
        }

        /// <summary>
        /// Get the raw mzdash content bytes for a dashboard (for upload to wheel).
        /// </summary>
        public byte[]? TryGetRawContent(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                if (_nameToHash.TryGetValue(name, out var hash))
                {
                    if (_rawContent.TryGetValue(hash, out var content))
                        return content;
                }
                if (TryGetNormalized(_folderRawContent, name, out var folderContent))
                    return folderContent;
            }
            return null;
        }

        /// <summary>
        /// Check if a hash is cached.
        /// </summary>
        public bool HasHash(string hash) { lock (_lock) return _byHash.ContainsKey(hash); }

        /// <summary>
        /// All cached dashboard names (wheel cache first, then folder, deduped).
        /// </summary>
        public IReadOnlyList<string> CachedNames
        {
            get
            {
                lock (_lock)
                {
                    var result = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in _nameToHash.Keys)
                        if (seen.Add(name)) result.Add(name);
                    foreach (var name in _folderProfiles.Keys)
                        if (seen.Add(name)) result.Add(name);
                    return result;
                }
            }
        }

        /// <summary>
        /// Scan a user-configured folder for .mzdash files and parse them.
        /// These act as a fallback library when wheel cache misses.
        /// </summary>
        public void LoadFromFolder(string folderPath)
        {
            lock (_lock)
            {
                _folderProfiles.Clear();
                _folderRawContent.Clear();
                _folderFilePaths.Clear();
                _folderHashes.Clear();
            }

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return;

            foreach (var file in Directory.GetFiles(folderPath, "*.mzdash", SearchOption.AllDirectories))
            {
                try
                {
                    var profile = _store.ParseMzdash(file);
                    if (profile == null) continue;

                    byte[] rawBytes = File.ReadAllBytes(file);
                    string hash;
                    using (var sha = SHA1.Create())
                    {
                        byte[] digest = sha.ComputeHash(rawBytes);
                        var sb = new StringBuilder(8);
                        for (int i = 0; i < 4; i++) sb.Append(digest[i].ToString("x2"));
                        hash = sb.ToString();
                    }

                    lock (_lock)
                    {
                        _folderProfiles[profile.Name] = profile;
                        _folderRawContent[profile.Name] = rawBytes;
                        _folderFilePaths[profile.Name] = file;
                        _folderHashes[profile.Name] = hash;
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] DashboardCache: failed to load folder file {file}: {ex.Message}");
                }
            }

            MozaLog.Debug($"[AZOM] DashboardCache: {FolderProfileCount} profiles loaded from folder '{folderPath}'");
        }

        /// <summary>
        /// Try exact key, then underscore↔space variants. Wheel reports
        /// "Rally V6" but mzdash filename is "Rally_V6.mzdash".
        /// </summary>
        private static bool TryGetNormalized<T>(Dictionary<string, T> dict, string name, out T value)
        {
            if (dict.TryGetValue(name, out value!)) return true;
            string alt = name.Replace(' ', '_');
            if (alt != name && dict.TryGetValue(alt, out value!)) return true;
            alt = name.Replace('_', ' ');
            if (alt != name && dict.TryGetValue(alt, out value!)) return true;
            value = default!;
            return false;
        }

        /// <summary>
        /// Get the original file path for a folder-loaded profile (for GetDashboardKey).
        /// </summary>
        public string? TryGetFolderFilePath(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                TryGetNormalized(_folderFilePaths, name, out var path);
                return path;
            }
        }

        /// <summary>
        /// Get the precomputed SHA1 hash (first 8 hex chars) for a folder-loaded profile.
        /// </summary>
        public string? TryGetFolderHash(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                _folderHashes.TryGetValue(name, out var hash);
                return hash;
            }
        }

        /// <summary>All folder-loaded dashboard names.</summary>
        public IReadOnlyList<string> FolderNames
        {
            get { lock (_lock) return _folderProfiles.Keys.ToList(); }
        }
    }
}
