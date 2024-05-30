﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using ME3TweaksModManager.modmanager.localizations;
using ME3TweaksModManager.modmanager.objects.mod.merge.v1;

namespace ME3TweaksModManager.modmanager.objects.mod.merge
{
    public class MergeModLoader
    {
        private const string MERGEMOD_MAGIC = @"M3MM";
        public static IMergeMod LoadMergeMod(Stream mergeFileStream, string filename, bool loadAssets, double modDescVersion)
        {
            if (mergeFileStream.ReadStringASCII(4) != MERGEMOD_MAGIC)
            {
                throw new Exception(M3L.GetString(M3L.string_mergeModFileDoesNotHaveCorrectMagicHeader));
            }

            var version = mergeFileStream.ReadByte();
            switch (version)
            {
                case 1:
                    return MergeMod1.ReadMergeMod(mergeFileStream, filename, loadAssets, modDescVersion);
                default:
                    return null;
            }
        }

        // How to specify version?
        public static string SerializeManifest(string inputfile, int version)
        {
            var outfile = Path.Combine(Directory.GetParent(inputfile).FullName, Path.GetFileNameWithoutExtension(inputfile) + @".m3m");
            M3Log.Information($@"M3MCompiler: Serializing {inputfile} to {outfile}");
            using MemoryStream fs = new MemoryStream();
            fs.WriteStringLatin1(MERGEMOD_MAGIC);
            fs.WriteByte((byte)version);
            IList<string> messages = null;
            switch (version)
            {
                case 1:
                    messages = MergeMod1.Serialize(fs, inputfile);
                    break;
                default:
                    throw new Exception(M3L.GetString(M3L.string_interp_unsupportedMergeModVersionVersionX, version));
            }

            if (messages != null)
            {
                // Will be caught higher up
                throw new Exception(M3L.GetString(M3L.string_interp_invalidMergeModManifestReason, string.Join('\n', messages)));
            }

            M3Log.Information($@"M3MCompiler: Writing final result to {outfile}");
            fs.WriteToFile(outfile);
            return outfile;
        }

        /// <summary>
        /// Decompiles an m3m file into its components.
        /// </summary>
        /// <param name="file">Filepath to decompile</param>
        public static void DecompileM3M(string file)
        {
            using var fs = File.OpenRead(file);
            var mm = LoadMergeMod(fs, file, true, 0.0); // Decompile does not depend on version number
            mm.ExtractToFolder(Directory.GetParent(file).FullName);

        }

        /// <summary>
        /// Gets the list of allowed files that a mergemod can target for a game.
        /// </summary>
        /// <returns>List of filanmes (with extensions). Startup may have _INT on the end!</returns>
        public static List<string> GetAllowedMergeTargetFilenames(MEGame game)
        {
            var safeFiles = EntryImporter.FilesSafeToImportFrom(game).ToList();
            if (game == MEGame.ME1)
            {
                safeFiles.Add(@"EntryMenu.SFM"); // ME1
            }
            else
            {
                safeFiles.Add(@"EntryMenu.pcc"); // ME2+
            }
            return safeFiles;
        }
    }
}
