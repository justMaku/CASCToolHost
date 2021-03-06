﻿using CASCToolHost.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CASCToolHost
{
    public static class CASC
    {
        public static Dictionary<MD5Hash, IndexEntry> indexDictionary = new Dictionary<MD5Hash, IndexEntry>(new MD5HashComparer());
        public static List<MD5Hash> indexNames = new List<MD5Hash>();

        public struct Build
        {
            public BuildConfigFile buildConfig;
            public CDNConfigFile cdnConfig;
            public EncodingFile encoding;
            public RootFile root;
            public DateTime loadedAt;
        }

        public static Build LoadBuild(string program, string buildConfigHash)
        {
            var cdnConfig = Database.GetCDNConfigByBuildConfig(buildConfigHash);
            if (string.IsNullOrEmpty(cdnConfig))
            {
                throw new Exception("Unable to locate CDNconfig for buildconfig " + buildConfigHash);
            }

            return LoadBuild(program, buildConfigHash, cdnConfig);
        }

        public static Build LoadBuild(string program, string buildConfigHash, string cdnConfigHash)
        {
            Logger.WriteLine("Loading build " + buildConfigHash + "..");

            var build = new Build();

            var cdnsFile = NGDP.GetCDNs(program);
            build.buildConfig = Config.GetBuildConfig(Path.Combine(CDN.cacheDir, cdnsFile.entries[0].path), buildConfigHash);
            build.cdnConfig = Config.GetCDNConfig(Path.Combine(CDN.cacheDir, cdnsFile.entries[0].path), cdnConfigHash);

            Logger.WriteLine("Loading encoding..");
            if (build.buildConfig.encodingSize == null || build.buildConfig.encodingSize.Count() < 2)
            {
                build.encoding = NGDP.GetEncoding("http://" + cdnsFile.entries[0].hosts[0] + "/" + cdnsFile.entries[0].path + "/", build.buildConfig.encoding[1].ToHexString(), 0);
            }
            else
            {
                build.encoding = NGDP.GetEncoding("http://" + cdnsFile.entries[0].hosts[0] + "/" + cdnsFile.entries[0].path + "/", build.buildConfig.encoding[1].ToHexString(), int.Parse(build.buildConfig.encodingSize[1]));
            }

            Logger.WriteLine("Loading root..");

            string rootHash;

            if (build.encoding.aEntries.TryGetValue(build.buildConfig.root, out var rootEntry))
            {
                rootHash = rootEntry.eKey.ToHexString().ToLower();
            }
            else
            {
                throw new KeyNotFoundException("Root encoding key not found!");
            }

            build.root = NGDP.GetRoot("http://" + cdnsFile.entries[0].hosts[0] + "/" + cdnsFile.entries[0].path + "/", rootHash, true);

            build.loadedAt = DateTime.Now;

            Logger.WriteLine("Loading indexes..");
            NGDP.GetIndexes(Path.Combine(CDN.cacheDir, cdnsFile.entries[0].path), build.cdnConfig.archives);

            return build;
        }

        public static bool FileExists(string buildConfig, string cdnConfig, uint filedataid)
        {
            var build = BuildCache.GetOrCreate(buildConfig);

            if (build.root.entriesFDID.ContainsKey(filedataid))
            {
                return true;
            }

            return false;
        }

        public static bool FileExists(string buildConfig, string cdnConfig, string filename)
        {
            var build = BuildCache.GetOrCreate(buildConfig);

            var hasher = new Jenkins96();
            var lookup = hasher.ComputeHash(filename, true);

            if (build.root.entriesLookup.ContainsKey(lookup))
            {
                return true;
            }

            return false;
        }

        public static byte[] GetFile(string buildConfig, string cdnConfig, uint filedataid)
        {
            var build = BuildCache.GetOrCreate(buildConfig, cdnConfig);

            var target = "";

            if (build.root.entriesFDID.TryGetValue(filedataid, out var entry))
            {
                RootEntry? prioritizedEntry = entry.FirstOrDefault(subentry =>
                        subentry.contentFlags.HasFlag(ContentFlags.LowViolence) == false && (subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) || subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                    );

                var selectedEntry = (prioritizedEntry != null) ? prioritizedEntry.Value : entry.First();
                target = selectedEntry.md5.ToHexString().ToLower();
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new FileNotFoundException("No file found in root for FileDataID " + filedataid);
            }

            return GetFile(buildConfig, cdnConfig, target);
        }

        public static byte[] GetFile(string buildConfig, string cdnConfig, string contenthash)
        {
            var build = BuildCache.GetOrCreate(buildConfig, cdnConfig);

            string target;

            if (build.encoding.aEntries.TryGetValue(contenthash.ToByteArray().ToMD5(), out var entry))
            {
                target = entry.eKey.ToHexString().ToLower();
            }
            else
            {
                throw new KeyNotFoundException("Key not found in encoding!");
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new FileNotFoundException("Unable to find file in encoding!");
            }

            return RetrieveFileBytes(target);
        }

        public static byte[] RetrieveFileBytes(string target, bool raw = false, string cdndir = "tpr/wow")
        {
            var unarchivedName = Path.Combine(CDN.cacheDir, cdndir, "data", target[0] + "" + target[1], target[2] + "" + target[3], target);

            if (File.Exists(unarchivedName))
            {
                if (!raw)
                {
                    return BLTE.Parse(File.ReadAllBytes(unarchivedName));
                }
                else
                {
                    return File.ReadAllBytes(unarchivedName);
                }
            }

            if (!indexDictionary.TryGetValue(target.ToByteArray().ToMD5(), out IndexEntry entry))
            {
                throw new Exception("Unable to find file in archives. File is not available!?");
            }

            var index = indexNames[(int)entry.indexID].ToHexString().ToLower();

            var archiveName = Path.Combine(CDN.cacheDir, cdndir, "data", index[0] + "" + index[1], index[2] + "" + index[3], index);
            if (!File.Exists(archiveName))
            {
                throw new FileNotFoundException("Unable to find archive " + index + " on disk!");
            }

            using (var stream = new FileStream(archiveName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var bin = new BinaryReader(stream))
            {
                bin.BaseStream.Position = entry.offset;
                try
                {
                    if (!raw)
                    {
                        return BLTE.Parse(bin.ReadBytes((int)entry.size));
                    }
                    else
                    {
                        return bin.ReadBytes((int)entry.size);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            }

            return new byte[0];
        }

        public static byte[] GetFileByFilename(string buildConfig, string cdnConfig, string filename)
        {
            var build = BuildCache.GetOrCreate(buildConfig, cdnConfig);

            var hasher = new Jenkins96();
            var lookup = hasher.ComputeHash(filename, true);
            var target = "";

            if (build.root.entriesLookup.TryGetValue(lookup, out var entry))
            {
                RootEntry? prioritizedEntry = entry.FirstOrDefault(subentry =>
                    subentry.contentFlags.HasFlag(ContentFlags.LowViolence) == false && (subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) || subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                );

                var selectedEntry = (prioritizedEntry != null) ? prioritizedEntry.Value : entry.First();
                target = selectedEntry.md5.ToHexString().ToLower();
            }

            if (string.IsNullOrEmpty(target))
            {
                var filedataid = Database.GetFileDataIDByFilename(filename);
                if (filedataid != 0)
                {
                    if (build.root.entriesFDID.TryGetValue(filedataid, out var fdidentry))
                    {
                        RootEntry? prioritizedEntry = fdidentry.FirstOrDefault(subentry =>
                            subentry.contentFlags.HasFlag(ContentFlags.LowViolence) == false && (subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) || subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                        );

                        var selectedEntry = (prioritizedEntry != null) ? prioritizedEntry.Value : fdidentry.First();
                        target = selectedEntry.md5.ToHexString().ToLower();
                    }
                }
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new FileNotFoundException("No file found in root for filename " + filename);
            }

            return GetFile(buildConfig, cdnConfig, target);
        }

        public static uint GetFileDataIDByFilename(string buildConfig, string cdnConfig, string filename)
        {
            var build = BuildCache.GetOrCreate(buildConfig, cdnConfig);

            var hasher = new Jenkins96();
            var lookup = hasher.ComputeHash(filename, true);

            if (build.root.entriesLookup.TryGetValue(lookup, out var entry))
            {
                return entry[0].fileDataID;
            }

            return 0;
        }

        public static uint[] GetFileDataIDsInBuild(string buildConfig, string cdnConfig)
        {
            var rootcdn = Database.GetRootCDNByBuildConfig(buildConfig);

            RootFile root;

            if (!string.IsNullOrEmpty(rootcdn))
            {
                root = NGDP.GetRoot(Path.Combine(SettingsManager.cacheDir, "tpr", "wow"), rootcdn, true);
            }
            else
            {
                if (string.IsNullOrEmpty(cdnConfig))
                {
                    cdnConfig = Database.GetCDNConfigByBuildConfig(buildConfig);
                }

                var build = BuildCache.GetOrCreate(buildConfig, cdnConfig);
                root = build.root;
            }

            return root.entriesFDID.Keys.ToArray();
        }
    }
}
