﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace CASCToolHost
{
    public static class CASC
    {
        public static Dictionary<string, Build> buildDictionary = new Dictionary<string, Build>();
        private static MD5HashComparer comparer = new MD5HashComparer();
        public static Dictionary<uint, Dictionary<MD5Hash, IndexEntry>> indexDictionary = new Dictionary<uint, Dictionary<MD5Hash, IndexEntry>>();
        public static List<MD5Hash> indexNames = new List<MD5Hash>();
        public static Dictionary<MD5Hash, uint> indexNameToIndexIDLookup = new Dictionary<MD5Hash, uint>(comparer);

        static CASC()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                CDN.cacheDir = "H:/";
            }
            else
            {
                CDN.cacheDir = "/var/www/bnet.marlam.in/";
            }

            CDN.client = new HttpClient();
        }

        public struct Build
        {
            public BuildConfigFile buildConfig;
            public CDNConfigFile cdnConfig;
            public EncodingFile encoding;
            public RootFile root;
        }

        public static void LoadBuild(string program, string buildConfigHash, string cdnConfigHash)
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
            var rootHash = "";

            foreach (var entry in build.encoding.aEntries)
            {
                if (comparer.Equals(entry.hash, build.buildConfig.root)) { rootHash = entry.key.ToHexString().ToLower(); break; }
            }

            build.root = NGDP.GetRoot("http://" + cdnsFile.entries[0].hosts[0] + "/" + cdnsFile.entries[0].path + "/", rootHash, true);

            Logger.WriteLine("Loading indexes..");
            NGDP.GetIndexes(Path.Combine(CDN.cacheDir, cdnsFile.entries[0].path), build.cdnConfig.archives);

            buildDictionary.Add(buildConfigHash, build);

            Logger.WriteLine("Loaded build!");
        }

        public static bool FileExists(string buildConfig, string cdnConfig, uint filedataid)
        {
            if (!buildDictionary.ContainsKey(buildConfig))
            {
                LoadBuild("wowt", buildConfig, cdnConfig);
            }

            var build = buildDictionary[buildConfig];

            foreach (var entry in build.root.entries)
            {
                if (entry.Value[0].fileDataID == filedataid)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool FileExists(string buildConfig, string cdnConfig, string filename)
        {
            if (!buildDictionary.ContainsKey(buildConfig))
            {
                LoadBuild("wowt", buildConfig, cdnConfig);
            }

            var build = buildDictionary[buildConfig];

            var hasher = new Jenkins96();
            var lookup = hasher.ComputeHash(filename, true);

            foreach (var entry in build.root.entries)
            {
                if (entry.Value[0].lookup == lookup)
                {
                    return true;
                }
            }

            return false;
        }

        public static byte[] GetFile(string buildConfig, string cdnConfig, uint filedataid)
        {
            if (!buildDictionary.ContainsKey(buildConfig))
            {
                LoadBuild("wowt", buildConfig, cdnConfig);
            }

            var build = buildDictionary[buildConfig];

            var target = "";

            foreach (var entry in build.root.entries)
            {
                if (entry.Value[0].fileDataID == filedataid)
                {
                    RootEntry? prioritizedEntry = entry.Value.First(subentry =>
                        subentry.contentFlags.HasFlag(ContentFlags.LowViolence) == false && (subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) || subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                    );

                    var selectedEntry = (prioritizedEntry != null) ? prioritizedEntry.Value : entry.Value.First();
                    target = selectedEntry.md5.ToHexString().ToLower();
                }
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new FileNotFoundException("No file found in root for FileDataID " + filedataid);
            }

            return GetFile(buildConfig, cdnConfig, target);
        }

        public static byte[] GetFile(string buildConfig, string cdnConfig, string contenthash)
        {
            if (!buildDictionary.ContainsKey(buildConfig))
            {
                LoadBuild("wowt", buildConfig, cdnConfig);
            }

            var build = buildDictionary[buildConfig];

            string target = "";

            foreach (var entry in build.encoding.aEntries)
            {
                var entryHash = entry.hash.ToHexString().ToLower();
                if (entryHash == contenthash)
                {
                    target = entry.key.ToHexString().ToLower();
                    break;
                }
            }

            if (string.IsNullOrEmpty(target))
            {
                throw new FileNotFoundException("Unable to find file in encoding!");
            }

            return RetrieveFileBytes(buildConfig, target);
        }

        public static byte[] RetrieveFileBytes(string buildConfig, string target, bool raw = false, string cdndir = "tpr/wow")
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

            if (!buildDictionary.ContainsKey(buildConfig))
            {
                throw new Exception("Build is not loaded!");
            }

            var build = buildDictionary[buildConfig];

            IndexEntry entry = new IndexEntry();

            foreach (var indexName in build.cdnConfig.archives)
            {
                indexDictionary[indexNameToIndexIDLookup[indexName]].TryGetValue(target.ToByteArray().ToMD5(), out entry);
                if (entry.size != 0) break;
            }

            if (entry.size == 0)
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
            if (!buildDictionary.ContainsKey(buildConfig))
            {
                LoadBuild("wowt", buildConfig, cdnConfig);
            }

            var build = buildDictionary[buildConfig];

            var hasher = new Jenkins96();
            var lookup = hasher.ComputeHash(filename, true);
            var target = "";

            foreach (var entry in build.root.entries)
            {
                if (entry.Value[0].lookup == lookup)
                {
                    RootEntry? prioritizedEntry = entry.Value.FirstOrDefault(subentry =>
                        subentry.contentFlags.HasFlag(ContentFlags.LowViolence) == false && (subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) || subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                    );

                    var selectedEntry = (prioritizedEntry != null) ? prioritizedEntry.Value : entry.Value.First();
                    target = selectedEntry.md5.ToHexString().ToLower();
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
            if (!buildDictionary.ContainsKey(buildConfig))
            {
                LoadBuild("wowt", buildConfig, cdnConfig);
            }

            var build = buildDictionary[buildConfig];

            var hasher = new Jenkins96();
            var lookup = hasher.ComputeHash(filename, true);

            foreach (var entry in build.root.entries)
            {
                if (entry.Value[0].lookup == lookup)
                {
                    return entry.Value[0].fileDataID;
                }
            }

            return 0;
        }
    }
}