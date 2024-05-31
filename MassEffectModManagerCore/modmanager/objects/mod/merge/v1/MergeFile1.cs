﻿using System.Diagnostics;
using System.Threading.Tasks;
using LegendaryExplorerCore.Misc;
using ME3TweaksCore.Helpers;
using ME3TweaksCore.Objects;
using ME3TweaksModManager.modmanager.localizations;
using Newtonsoft.Json;

namespace ME3TweaksModManager.modmanager.objects.mod.merge.v1
{
    public class MergeFile1 : IMergeModCommentable
    {

        /// <summary>
        /// The target file, e.g. SFXGame.pcc
        /// </summary>
        [JsonProperty(@"filename")]
        public string FileName { get; set; }

        /// <summary>
        /// The changes to apply to the files
        /// </summary>
        [JsonProperty(@"changes")]
        public List<MergeFileChange1> MergeChanges { get; set; }

        /// <summary>
        /// If changes should be applied to all localized versions of the target file
        /// </summary>
        [JsonProperty(@"applytoalllocalizations")] 
        public bool ApplyToAllLocalizations { get; set; }

        /// <summary>
        /// The comment on this field. Optional.
        /// </summary>
        [JsonProperty("comment")]
        public string Comment { get; set; }

        /// <summary>
        /// Parent of this file
        /// </summary>
        [JsonIgnore] 
        public MergeMod1 Parent;

        /// <summary>
        /// The owning mod for this merge file (same as parent)
        /// </summary>
        [JsonIgnore]
        public MergeMod1 OwningMM => Parent;

        public void SetupParent(MergeMod1 mm)
        {
            Parent = mm;
            foreach (var mc in MergeChanges)
            {
                mc.SetupParent(this);
            }
        }

        /// <summary>
        /// Applies the changes this merge file describes
        /// </summary>
        /// <param name="gameTarget"></param>
        /// <param name="loadedFiles"></param>
        /// <param name="associatedMod"></param>
        /// <param name="mergeWeightDelegate">Callback to submit completed weight for progress tracking</param>
        /// <param name="addTrackedFileDelegate">Callback to submit a new text string to display</param>
        /// <exception cref="Exception"></exception>
        public void ApplyChanges(GameTarget gameTarget, CaseInsensitiveDictionary<string> loadedFiles, Mod associatedMod, Action<int> mergeWeightDelegate, CaseInsensitiveConcurrentDictionary<string> originalFileMD5Map, Action<string, string> addTrackedFileDelegate = null)
        {
            var targetFiles = new SortedSet<string>();
            if (ApplyToAllLocalizations)
            {
                var targetnameBase = Path.GetFileNameWithoutExtension(FileName).StripUnrealLocalization();
                var targetExtension = Path.GetExtension(FileName);
                var localizations = GameLanguage.GetLanguagesForGame(associatedMod.Game);

                // Ensure end name is not present on base
                //foreach (var l in localizations)
                //{
                //    if (targetnameBase.EndsWith($@"_{l.FileCode}", StringComparison.InvariantCultureIgnoreCase))
                //        targetnameBase = targetnameBase.Substring(0, targetnameBase.Length - (l.FileCode.Length + 1)); //_FileCode
                //}

                var hasOneFile = false;
                foreach (var l in localizations)
                {
                    var targetname = $@"{targetnameBase}_{l.FileCode}{targetExtension}";
                    hasOneFile |= addMergeTarget(targetname, loadedFiles, targetFiles, mergeWeightDelegate);
                }

                if (!hasOneFile)
                {
                    throw new Exception(M3L.GetString(M3L.string_interp_mergefile_noLocalizedFiles, FileName));
                }
            }
            else
            {
                addMergeTarget(FileName, loadedFiles, targetFiles, mergeWeightDelegate);
            }

            var mac = new MergeAssetCache1();

            // Change 04/15/2023: Multi-core support for multiple changes from a single 'file'. If it has multiple localizations they will all be different
            // So in theory this should work...
            var numCores = !Settings.LogModInstallation ? 4 : 1;
            var syncObj = new object();
            Parallel.ForEach(targetFiles, new ParallelOptions() { MaxDegreeOfParallelism = numCores }, f =>
            //{
            //foreach (string f in targetFiles)
            {
                M3Log.Information($@"Opening package {f}");
#if DEBUG
                var sw = Stopwatch.StartNew();
#endif
                // Open as memorystream as we need to hash this file for tracking
                using var ms = new MemoryStream(File.ReadAllBytes(f));

                if (!originalFileMD5Map.TryGetValue(f, out var existingMD5))
                {
                    existingMD5 = MUtilities.CalculateHash(ms);
                    originalFileMD5Map[f] = existingMD5; // Set
                }

                var package = MEPackageHandler.OpenMEPackageFromStream(ms, f);
#if DEBUG
                Debug.WriteLine($@"Opening package {f} took {sw.ElapsedMilliseconds} ms");
#endif
                foreach (var pc in MergeChanges)
                {
                    pc.ApplyChanges(package, mac, associatedMod, gameTarget, mergeWeightDelegate);
                }

                var track = package.IsModified;
                if (package.IsModified)
                {
                    M3Log.Information($@"Saving package {package.FilePath}");
#if DEBUG
                    sw.Stop();
#endif
                    package.Save(savePath: f, compress: true);
#if DEBUG
                    Debug.WriteLine($@"Saving package {f} took {sw.ElapsedMilliseconds} ms");
#endif
                }
                else
                {
                    M3Log.Information($@"Package {package.FilePath} was not modified. This change is likely already installed, not saving package");
                }

                if (track)
                {
                    // Add to basegame database.
                    // It uses a list. It is not thread safe. Use a lock
                    lock (syncObj)
                    {
                        addTrackedFileDelegate?.Invoke(existingMD5, f);
                    }
                }
            });
        }

        private bool addMergeTarget(string fileName, CaseInsensitiveDictionary<string> loadedFiles,
            SortedSet<string> targetFiles, Action<int> mergeWeightDelegate)
        {
            if (loadedFiles.TryGetValue(fileName, out string fullpath))
            {
                targetFiles.Add(fullpath);
                return true;
            }
            M3Log.Warning($@"File not found in game: {fileName}, skipping...");
            mergeWeightDelegate?.Invoke(GetMergeWeightSingle()); // Skip this weight
            return false;
        }

        public int GetMergeCount() => ApplyToAllLocalizations ? GameLanguage.GetLanguagesForGame(OwningMM.Game).Length : 1;


        /// <summary>
        /// Gets the merge weight for a single file (not to all localizations)
        /// </summary>
        /// <returns></returns>
        public int GetMergeWeightSingle()
        {
            var weight = 0;

            foreach (var v in MergeChanges)
            {
                weight += v.GetMergeWeight();
            }

            Debug.WriteLine($@"Single merge weight for {FileName}: weight");
            return weight;
        }

        public int GetMergeWeight()
        {
            // Merge weight is the number of files to merge multiplied by the amount of a single merge
            var multiplier = ApplyToAllLocalizations ? GameLanguage.GetLanguagesForGame(OwningMM.Game).Length : 1;
            return multiplier * GetMergeWeightSingle();
        }


        /// <summary>
        /// Validates this MergeFile. Throws an exception if the validation fails.
        /// </summary>
        public void Validate()
        {
            if (FileName == null) throw new Exception(M3L.GetString(M3L.string_filenameCannotBeNullInAMergeManifestFile!));
            var safeFiles = MergeModLoader.GetAllowedMergeTargetFilenames(Parent.Game);

            if (OwningMM.MergeModVersion >= 2)
            {
                // Mod Manager 9.0 (m3m v2): Support targeting specific localizations of files, e.g. Specific changes for Startup_POL
                if (!safeFiles.Any(x => FileName.StripUnrealLocalization().StartsWith(Path.GetFileNameWithoutExtension(x.StripUnrealLocalization()), StringComparison.InvariantCultureIgnoreCase)))
                {
                    // Does this catch DLC startups? 
                    throw new Exception(M3L.GetString(M3L.string_interp_targetingNonStartupFile, FileName));
                }
            }
            else
            {
                // Backwards compatibility - this is how Mod Manager 8.2.3 and below parsed names.
                if (!safeFiles.Any(x => FileName.StartsWith(Path.GetFileNameWithoutExtension(x), StringComparison.InvariantCultureIgnoreCase)))
                {
                    // Does this catch DLC startups? 
                    throw new Exception(M3L.GetString(M3L.string_interp_targetingNonStartupFile, FileName));
                }
            }

            foreach (var mc in MergeChanges)
            {
                mc.Validate();
            }
        }
    }
}
