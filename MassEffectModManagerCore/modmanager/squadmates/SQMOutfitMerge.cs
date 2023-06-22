﻿using System.Xml.Linq;
using LegendaryExplorerCore.Coalesced;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.Extensions.Collections.Generic;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.UnrealScript;
using LegendaryExplorerCore.UnrealScript.Compiling.Errors;
using ME3TweaksCore.Config;
using ME3TweaksCore.GameFilesystem;
using ME3TweaksCore.Helpers;
using ME3TweaksCoreWPF.Targets;
using ME3TweaksModManager.modmanager.gameini;
using ME3TweaksModManager.modmanager.localizations;
using ME3TweaksModManager.modmanager.merge.dlc;
using ME3TweaksModManager.modmanager.objects;
using ME3TweaksModManager.modmanager.objects.merge.squadmate;
using Newtonsoft.Json;

namespace ME3TweaksModManager.modmanager.squadmates
{
    public class SQMOutfitMerge
    {
        public const string SQUADMATE_MERGE_MANIFEST_FILE = @"SquadmateMergeInfo.sqm";
        public const int STARTING_OUTFIT_CONDITIONAL = 10000;

        internal class SquadmateMergeInfo
        {
            [JsonProperty(@"game")]
            public MEGame Game { get; set; }

            [JsonProperty(@"outfits")]
            public List<SquadmateInfoSingle> Outfits { get; set; }

            public bool Validate(string dlcName, GameTargetWPF target, CaseInsensitiveDictionary<string> loadedFiles)
            {
                foreach (var outfit in Outfits)
                {
                    // Check packages
                    if (!loadedFiles.ContainsKey($@"{outfit.HenchPackage}.pcc"))
                    {
                        M3Log.Error($@"SquadmateMergeInfo failed validation: {outfit.HenchPackage}.pcc not found in game");
                        return false;
                    }

                    if (Game.IsGame3())
                    {
                        if (!loadedFiles.ContainsKey($@"{outfit.HenchPackage}_Explore.pcc"))
                        {
                            M3Log.Error($@"SquadmateMergeInfo failed validation: {outfit.HenchPackage}_Explore.pcc not found in game");
                            return false;
                        }
                    }

                    if (!loadedFiles.ContainsKey($@"SFXHenchImages_{dlcName}.pcc"))
                    {
                        M3Log.Error($@"SquadmateMergeInfo failed validation: SFXHenchImages_{dlcName}.pcc not found in game");
                        return false;
                    }
                }

                return true;
            }
        }

        private static StructProperty GeneratePlotStreamingElement(string packageName, int conditionalNum)
        {
            PropertyCollection pc = new PropertyCollection();
            pc.AddOrReplaceProp(new NameProperty(packageName, @"ChunkName"));
            pc.AddOrReplaceProp(new IntProperty(conditionalNum, @"Conditional"));
            pc.AddOrReplaceProp(new BoolProperty(false, @"bFallback"));
            pc.AddOrReplaceProp(new NoneProperty());

            return new StructProperty(@"PlotStreamingElement", pc);
        }

        private static int GetSquadmateOutfitInt(string squadmateName, MEGame game)
        {
            M3Log.Information($@"SQMMERGE: Generating outfit int for {game} {squadmateName}");
            if (game.IsGame2())
            {
                // This is if we ever implement it into Game 2
                switch (squadmateName)
                {
                    case @"Convict": return 314;
                    case @"Garrus": return 318;
                    case @"Geth": return 315;
                    case @"Grunt": return 322;
                    case @"Leading": return 313;
                    case @"Mystic": return 323;
                    case @"Professor": return 321;
                    case @"Tali": return 320;
                    case @"Thief": return 317;
                    case @"Veteran": return 324;
                    case @"Vixen": return 312;

                    // LOTSB
                    case @"Liara": return 312;
                }
            }
            else if (game.IsGame3())
            {
                switch (squadmateName)
                {
                    case @"Liara": return 10152;
                    case @"Kaidan": return 10153;
                    case @"Ashley": return 10154;
                    case @"Garrus": return 10155;
                    case @"EDI": return 10156;
                    case @"Prothean": return 10157;
                    case @"Marine": return 10158;
                    case @"Tali": return 10214;
                        // case @"Wrex": return ??; Wrex outfit can't be changed, its hardcoded to 13, which is TRUE
                }
            }

            throw new Exception(M3L.GetString(M3L.string_interp_invalidHenchNameSquadmateNameValueIsCaseSensitive, squadmateName));
        }

        /// <summary>
        /// Returns if the specified target has any squadmate outfit merge files.
        /// </summary>
        /// <param name="target"></param>
        public static bool NeedsMerged(GameTargetWPF target)
        {
            if (!target.Game.IsGame3() && target.Game != MEGame.LE2) return false;
            var sqmSupercedances = M3Directories.GetFileSupercedances(target, new[] { @".sqm" });
            return sqmSupercedances.TryGetValue(SQUADMATE_MERGE_MANIFEST_FILE, out var infoList) && infoList.Count > 0;
        }

        /// <summary>
        /// Generates squadmate outfit information for Game 3 and LE2. The merge DLC must be already generated.
        /// </summary>
        /// <param name="mergeDLC"></param>
        /// <exception cref="Exception"></exception>
        public static void RunSquadmateOutfitMerge(M3MergeDLC mergeDLC)
        {
            if (!mergeDLC.Generated)
                return; // Do not run on non-generated. It may be that a prior check determined this merge was not necessary 

            var loadedFiles = MELoadedFiles.GetFilesLoadedInGame(mergeDLC.Target.Game, gameRootOverride: mergeDLC.Target.TargetPath);
            //var mergeFiles = loadedFiles.Where(x =>
            //    x.Key.StartsWith(@"BioH_") && x.Key.Contains(@"_DLC_MOD_") && x.Key.EndsWith(@".pcc") && !x.Key.Contains(@"_LOC_") && !x.Key.Contains(@"_Explore."));

            M3Log.Information(@"SQMMERGE: Building BioP_Global");
            var appearanceInfo = new CaseInsensitiveDictionary<List<SquadmateInfoSingle>>();

            int appearanceId = mergeDLC.Target.Game.IsGame3() ? 255 : 3; // starting // LE2 is 0-8, LE3 does not care
            int currentConditional = STARTING_OUTFIT_CONDITIONAL;

            // Scan squadmate merge files
            var sqmSupercedances = M3Directories.GetFileSupercedances(mergeDLC.Target, new[] { @".sqm" });
            if (sqmSupercedances.TryGetValue(SQUADMATE_MERGE_MANIFEST_FILE, out var infoList))
            {
                infoList.Reverse();
                foreach (var dlc in infoList)
                {
                    M3Log.Information($@"SQMMERGE: Processing {dlc}");

                    var jsonFile = Path.Combine(M3Directories.GetDLCPath(mergeDLC.Target), dlc, mergeDLC.Target.Game.CookedDirName(), SQUADMATE_MERGE_MANIFEST_FILE);
                    var infoPackage = JsonConvert.DeserializeObject<SquadmateMergeInfo>(File.ReadAllText(jsonFile));
                    if (!infoPackage.Validate(dlc, mergeDLC.Target, loadedFiles))
                    {
                        continue; // skip this
                    }

                    // Enumerate all outfits listed for a single squadmate
                    foreach (var outfit in infoPackage.Outfits)
                    {
                        List<SquadmateInfoSingle> list;

                        // See if we already have an outfit list for this squadmate, maybe from another mod...
                        if (!appearanceInfo.TryGetValue(outfit.HenchName, out list))
                        {
                            list = new List<SquadmateInfoSingle>();
                            appearanceInfo[outfit.HenchName] = list;
                        }

                        outfit.ConditionalIndex = currentConditional++; // This is always incremented, so it might appear out of order in game files depending on how mod order is processed, that should be okay though.
                        outfit.AppearanceId = appearanceId++; // may need adjusted
                        outfit.DLCName = dlc;
                        list.Add(outfit);
                        M3Log.Information($@"SQMMERGE: ConditionalIndex for {outfit.HenchName} appearanceid {outfit.AppearanceId}: {outfit.ConditionalIndex}");
                    }

                    //Debug.WriteLine("hi");
                }
            }

            if (appearanceInfo.Any())
            {
                var biopGlobal = MEPackageHandler.OpenMEPackage(loadedFiles[@"BioP_Global.pcc"]);
                var lsk = biopGlobal.Exports.FirstOrDefault(x => x.ClassName == @"LevelStreamingKismet");
                var persistentLevel = biopGlobal.FindExport(@"TheWorld.PersistentLevel");

                // Clone LevelStreamingKismets
                foreach (var sqm in appearanceInfo.Values)
                {
                    foreach (var outfit in sqm)
                    {
                        var fName = outfit.HenchPackage;
                        var newLSK = EntryCloner.CloneEntry(lsk);
                        newLSK.WriteProperty(new NameProperty(fName, @"PackageName"));

                        if (mergeDLC.Target.Game.IsGame3())
                        {
                            // Game 3 has _Explore files too
                            fName += @"_Explore";
                            newLSK = EntryCloner.CloneEntry(lsk);
                            newLSK.WriteProperty(new NameProperty(fName, @"PackageName"));
                        }
                    }
                }

                // Update BioWorldInfo
                // Doesn't have consistent number so we can't find it by instanced full path
                var bioWorldInfo = biopGlobal.Exports.FirstOrDefault(x => x.ClassName == @"BioWorldInfo");

                var props = bioWorldInfo.GetProperties();

                // Update Plot Streaming
                var plotStreaming = props.GetProp<ArrayProperty<StructProperty>>(@"PlotStreaming");
                foreach (var sqm in appearanceInfo.Values)
                {
                    foreach (var outfit in sqm)
                    {
                        // find item to add to
                        buildPlotElementObject(plotStreaming, outfit, mergeDLC.Target.Game, false);
                        if (mergeDLC.Target.Game.IsGame3())
                        {
                            buildPlotElementObject(plotStreaming, outfit, mergeDLC.Target.Game, true);
                        }
                    }
                }


                // Update StreamingLevels
                var streamingLevels = props.GetProp<ArrayProperty<ObjectProperty>>(@"StreamingLevels");
                streamingLevels.ReplaceAll(biopGlobal.Exports.Where(x => x.ClassName == @"LevelStreamingKismet").Select(x => new ObjectProperty(x)));

                bioWorldInfo.WriteProperties(props);

                // Save BioP_Global into DLC
                var cookedDir = Path.Combine(M3Directories.GetDLCPath(mergeDLC.Target), M3MergeDLC.MERGE_DLC_FOLDERNAME, mergeDLC.Target.Game.CookedDirName());
                var outP = Path.Combine(cookedDir, @"BioP_Global.pcc");
                biopGlobal.Save(outP);

                // Generate conditionals file
                if (mergeDLC.Target.Game.IsGame3())
                {
                    // ME3/LE3
                    CNDFile cnd = new CNDFile();
                    cnd.ConditionalEntries = new List<CNDFile.ConditionalEntry>();

                    foreach (var sqm in appearanceInfo.Values)
                    {
                        foreach (var outfit in sqm)
                        {
                            var scText = $@"(plot.ints[{GetSquadmateOutfitInt(outfit.HenchName, mergeDLC.Target.Game)}] == i{outfit.MemberAppearanceValue})";
                            var compiled = ME3ConditionalsCompiler.Compile(scText);
                            cnd.ConditionalEntries.Add(new CNDFile.ConditionalEntry()
                            { Data = compiled, ID = outfit.ConditionalIndex });
                        }
                    }

                    cnd.ToFile(Path.Combine(cookedDir, $@"Conditionals{M3MergeDLC.MERGE_DLC_FOLDERNAME}.cnd"));
                }
                else if (mergeDLC.Target.Game.IsGame2())
                {
                    // LE2
                    var startupF = Path.Combine(cookedDir, $@"Startup_{M3MergeDLC.MERGE_DLC_FOLDERNAME}.pcc");
                    var startup = MEPackageHandler.OpenMEPackageFromStream(M3Utilities.GetResourceStream($@"ME3TweaksModManager.modmanager.merge.dlc.{mergeDLC.Target.Game}.Startup_{M3MergeDLC.MERGE_DLC_FOLDERNAME}.pcc"), $@"Startup_{M3MergeDLC.MERGE_DLC_FOLDERNAME}.pcc");
                    var conditionalClass = startup.FindExport($@"PlotManager{M3MergeDLC.MERGE_DLC_FOLDERNAME}.BioAutoConditionals");

                    // Add Conditional Functions
                    FileLib fl = new FileLib(startup);
                    bool initialized = fl.Initialize(new RelativePackageCache() { RootPath = mergeDLC.Target.GetBioGamePath() }, mergeDLC.Target.TargetPath);
                    if (!initialized)
                    {
                        throw new Exception(@"FileLib for script update could not initialize, cannot install conditionals");
                    }


                    var conditionalsClass = startup.FindExport($@"PlotManager{M3MergeDLC.MERGE_DLC_FOLDERNAME}.BioAutoConditionals");
                    var scTextOrig = new StreamReader(M3Utilities.GetResourceStream(
                            $@"ME3TweaksModManager.modmanager.squadmates.{mergeDLC.Target.Game}.HasOutfitOnConditional.txt"))
                        .ReadToEnd();
                    foreach (var sqm in appearanceInfo.Values)
                    {
                        foreach (var outfit in sqm)
                        {
                            var scText = scTextOrig.Replace(@"%CONDITIONALNUM%", outfit.ConditionalIndex.ToString());
                            scText = scText.Replace(@"%SQUADMATEOUTFITPLOTINT%", GetSquadmateOutfitInt(outfit.HenchName, MEGame.LE2).ToString());
                            scText = scText.Replace(@"%OUTFITINDEX%", outfit.MemberAppearanceValue.ToString());
                            
                            MessageLog log = UnrealScriptCompiler.AddOrReplaceInClass(conditionalClass, scText, fl);
                            if (log.AllErrors.Any())
                            {
                                M3Log.Error($@"Error compiling function F{outfit.ConditionalIndex}:");
                                foreach (var l in log.AllErrors)
                                {
                                    M3Log.Error(l.Message);
                                }

                                throw new Exception(M3L.GetString(M3L.string_interp_errorCompilingConditionalFunction, $@"F{outfit.ConditionalIndex}", string.Join('\n', log.AllErrors.Select(x => x.Message))));
                            }
                        }
                    }


                    // Relink the conditionals chain
                    UClass uc = ObjectBinary.From<UClass>(conditionalClass);
                    uc.UpdateLocalFunctions();
                    uc.UpdateChildrenChain();
                    conditionalClass.WriteBinary(uc);

                    startup.Save(startupF);
                }


                // Add startup package, member appearances
                if (mergeDLC.Target.Game.IsGame2())
                {
                    var configBundle = ConfigAssetBundle.FromDLCFolder(mergeDLC.Target.Game, cookedDir, M3MergeDLC.MERGE_DLC_FOLDERNAME);

                    // add startup file
                    var bioEngine = configBundle.GetAsset(@"BIOEngine.ini");
                    var startupSection = bioEngine.GetOrAddSection(@"Engine.StartupPackages");
                    startupSection.AddEntry(new CoalesceProperty(@"DLCStartupPackage", new CoalesceValue($@"Startup_{M3MergeDLC.MERGE_DLC_FOLDERNAME}", CoalesceParseAction.AddUnique)));
                    startupSection.AddEntry(new CoalesceProperty(@"Package", new CoalesceValue($@"PlotManager{M3MergeDLC.MERGE_DLC_FOLDERNAME}", CoalesceParseAction.AddUnique)));

                    // Add conditionals 
                    var bioGame = configBundle.GetAsset(@"BIOGame.ini");
                    var bioWorldInfoConfig = bioGame.GetOrAddSection(@"SFXGame.BioWorldInfo");
                    bioWorldInfoConfig.AddEntry(new CoalesceProperty(@"ConditionalClasses", new CoalesceValue($@"PlotManager{M3MergeDLC.MERGE_DLC_FOLDERNAME}.BioAutoConditionals", CoalesceParseAction.AddUnique)));

                    // Add appearances to list
                    var bioUi = configBundle.GetAsset(@"BIOUI.ini");
                    var partySelectionSection = bioUi.GetOrAddSection(@"SFXGame.BioSFHandler_PartySelection");

                    foreach (var sqm in appearanceInfo.Values)
                    {
                        foreach (var outfit in sqm)
                        {
                            //lstAppearances = (Tag = hench_tali, AddAppearance = 3, PlotFlag = -1);
                            var properties = new Dictionary<string, string>();
                            properties[@"Tag"] = $@"hench_{outfit.HenchName.ToLower()}";
                            properties[@"AddAppearance"] = outfit.AppearanceId.ToString();
                            properties[@"PlotFlag"] = @"-1";
                            var appearanceStruct = StringStructParser.BuildCommaSeparatedSplitValueList(properties);
                            partySelectionSection.AddEntry(new CoalesceProperty(@"lstAppearances", new CoalesceValue(appearanceStruct, CoalesceParseAction.AddUnique)));
                        }
                    }

                    configBundle.CommitDLCAssets();
                }
                else if (mergeDLC.Target.Game.IsGame3())
                {
                    var mergeCoalFile = Path.Combine(M3Directories.GetDLCPath(mergeDLC.Target), M3MergeDLC.MERGE_DLC_FOLDERNAME, mergeDLC.Target.Game.CookedDirName(), $@"Default_{M3MergeDLC.MERGE_DLC_FOLDERNAME}.bin");
                    var mergeCoal = CoalescedConverter.DecompileGame3ToMemory(new MemoryStream(File.ReadAllBytes(mergeCoalFile)));

                    // Member appearances
                    var bioUiDoc = XDocument.Parse(mergeCoal[@"BIOUI.xml"]);

                    foreach (var sqm in appearanceInfo.Values)
                    {
                        foreach (var outfit in sqm)
                        {
                            var entry = new Game3CoalescedValueEntry()
                            {
                                Section = @"sfxgame.sfxguidata_teamselect",
                                Name = @"selectappearances",
                                Type = 3,
                                Value = StringStructParser.BuildCommaSeparatedSplitValueList(outfit.ToPropertyDictionary(), @"AvailableImage", @"HighlightImage", @"DeadImage", @"SilhouetteImage")
                            };
                            Game3CoalescedHelper.AddArrayEntry(bioUiDoc, entry);
                        }
                    }

                    mergeCoal[@"BIOUI.xml"] = bioUiDoc.ToString();

                    // Dynamic load mapping
                    var bioEngineDoc = XDocument.Parse(mergeCoal[@"BIOEngine.xml"]);

                    foreach (var sqm in appearanceInfo.Values)
                    {
                        foreach (var outfit in sqm)
                        {
                            //
                            // * <Section name="sfxgame.sfxengine">
                            // <Property name="dynamicloadmapping">
                            // <Value type="3">(ObjectName="BIOG_GesturesConfigDLC.RuntimeData",SeekFreePackageName="GesturesConfigDLC")</Value>

                            var entry = new Game3CoalescedValueEntry()
                            {
                                Section = @"sfxgame.sfxengine",
                                Name = @"dynamicloadmapping",
                                Type = 3
                            };

                            entry.Values.Add($"(ObjectName=\"{outfit.AvailableImage}\",SeekFreePackageName=\"SFXHenchImages_{outfit.DLCName}\")"); // do not localize
                            entry.Values.Add($"(ObjectName=\"{outfit.HighlightImage}\",SeekFreePackageName=\"SFXHenchImages_{outfit.DLCName}\")"); // do not localize
                            Game3CoalescedHelper.AddArrayEntry(bioEngineDoc, entry);
                        }
                    }

                    mergeCoal[@"BIOEngine.xml"] = bioEngineDoc.ToString();


                    CoalescedConverter.CompileFromMemory(mergeCoal).WriteToFile(mergeCoalFile);
                }
            }
        }

        private static void buildPlotElementObject(ArrayProperty<StructProperty> plotStreaming, SquadmateInfoSingle sqm, MEGame game, bool isExplore)
        {
            var fName = sqm.HenchPackage;
            var virtualChunk = $@"BioH_{sqm.HenchName}";
            if (game.IsGame3() && isExplore)
            {
                fName += @"_Explore";
                virtualChunk += @"_Explore";
            }

            var element = plotStreaming.FirstOrDefault(x => x.GetProp<NameProperty>(@"VirtualChunkName").Value == virtualChunk);
            if (element != null)
            {
                var elem = element.GetProp<ArrayProperty<StructProperty>>(@"Elements");
                sqm.MemberAppearanceValue = elem.Count;
                elem.Add(GeneratePlotStreamingElement(fName, sqm.ConditionalIndex));
            }
        }
    }
}
