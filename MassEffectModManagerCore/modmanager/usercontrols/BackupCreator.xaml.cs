﻿using MassEffectModManagerCore.modmanager.objects;
using MassEffectModManagerCore.ui;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

using MassEffectModManagerCore.modmanager.helpers;
using System.Windows.Input;
using System.Diagnostics;
using Serilog;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.IO;
using MassEffectModManagerCore.GameDirectories;
using MassEffectModManagerCore.modmanager.windows;
using Microsoft.AppCenter.Analytics;
using Microsoft.Win32;
using MassEffectModManagerCore.modmanager.localizations;
using ByteSizeLib;
using Microsoft.AppCenter.Crashes;

namespace MassEffectModManagerCore.modmanager.usercontrols
{
    /// <summary>
    /// Interaction logic for BackupRestoreManager.xaml
    /// </summary>
    public partial class BackupCreator : MMBusyPanelBase
    {
        public bool AnyGameMissingBackup => !BackupService.ME1BackedUp || !BackupService.ME2BackedUp || !BackupService.ME3BackedUp;
        public ObservableCollectionExtended<GameBackup> GameBackups { get; } = new ObservableCollectionExtended<GameBackup>();

        //public GameBackup ME3Backup { get; set; }
        //public GameBackup ME2Backup { get; set; }
        //public GameBackup ME1Backup { get; set; }
        private List<GameTarget> targetsList;
        public BackupCreator(List<GameTarget> targetsList, GameTarget selectedTarget, Window window)
        {
            DataContext = this;
            this.targetsList = targetsList;
            LoadCommands();
            InitializeComponent();

            //InstallationTargets_ComboBox.SelectedItem = selectedTarget;
        }

        public ICommand CloseCommand { get; set; }

        private void LoadCommands()
        {
            CloseCommand = new GenericCommand(() => OnClosing(DataEventArgs.Empty), CanClose);
        }

        private bool CanClose() => !GameBackups.Any(x => x.BackupInProgress);

        public override void HandleKeyPress(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && CanClose())
            {
                OnClosing(DataEventArgs.Empty);
            }
        }

        public override void OnPanelVisible()
        {
            GameBackups.Add(new GameBackup(Mod.MEGame.ME1, targetsList.Where(x => x.Game == Mod.MEGame.ME1), mainwindow));
            GameBackups.Add(new GameBackup(Mod.MEGame.ME2, targetsList.Where(x => x.Game == Mod.MEGame.ME2), mainwindow));
            GameBackups.Add(new GameBackup(Mod.MEGame.ME3, targetsList.Where(x => x.Game == Mod.MEGame.ME3), mainwindow));
        }

        public class GameBackup : INotifyPropertyChanged
        {
            private Mod.MEGame Game;
            public ObservableCollectionExtended<GameTarget> AvailableBackupSources { get; } = new ObservableCollectionExtended<GameTarget>();
            private MainWindow window;
            public GameBackup(Mod.MEGame game, IEnumerable<GameTarget> availableBackupSources, MainWindow window)
            {
                this.window = window;
                this.Game = game;
                this.AvailableBackupSources.AddRange(availableBackupSources);
                LoadCommands();
                switch (Game)
                {
                    case Mod.MEGame.ME1:
                        GameTitle = @"Mass Effect";
                        GameIconSource = @"/images/gameicons/ME1_48.ico";
                        break;
                    case Mod.MEGame.ME2:
                        GameTitle = @"Mass Effect 2";
                        GameIconSource = @"/images/gameicons/ME2_48.ico";
                        break;
                    case Mod.MEGame.ME3:
                        GameTitle = @"Mass Effect 3";
                        GameIconSource = @"/images/gameicons/ME3_48.ico";
                        break;
                }
                ResetBackupStatus();
            }

            private void LoadCommands()
            {
                BackupButtonCommand = new GenericCommand(BeginBackup, CanBeginBackup);
            }

            private bool CanBeginBackup()
            {
                return BackupSourceTarget != null && !BackupInProgress;
            }

            private void BeginBackup()
            {
                if (Utilities.IsGameRunning(BackupSourceTarget.Game))
                {
                    M3L.ShowDialog(window, M3L.GetString(M3L.string_interp_cannotBackupGameWhileRunning, Utilities.GetGameName(BackupSourceTarget.Game)), M3L.GetString(M3L.string_gameRunning), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                NamedBackgroundWorker bw = new NamedBackgroundWorker(Game.ToString() + @"Backup");
                bw.DoWork += (a, b) =>
                {
                    BackupInProgress = true;
                    List<string> nonVanillaFiles = new List<string>();
                    void nonVanillaFileFoundCallback(string filepath)
                    {
                        Log.Error($@"Non-vanilla file found: {filepath}");
                        nonVanillaFiles.Add(filepath);
                    }

                    List<string> inconsistentDLC = new List<string>();
                    void inconsistentDLCFoundCallback(string filepath)
                    {
                        if (BackupSourceTarget.Supported)
                        {
                            Log.Error($@"DLC is in an inconsistent state: {filepath}");
                            inconsistentDLC.Add(filepath);
                        }
                        else
                        {
                            Log.Error(@"Detected an inconsistent DLC, likely due to an unofficial copy of the game");
                        }
                    }
                    ProgressVisible = true;
                    ProgressIndeterminate = true;
                    BackupStatus = M3L.GetString(M3L.string_validatingBackupSource);
                    VanillaDatabaseService.LoadDatabaseFor(Game, BackupSourceTarget.IsPolishME1);
                    bool isVanilla = VanillaDatabaseService.ValidateTargetAgainstVanilla(BackupSourceTarget, nonVanillaFileFoundCallback);
                    bool isDLCConsistent = VanillaDatabaseService.ValidateTargetDLCConsistency(BackupSourceTarget, inconsistentDLCCallback: inconsistentDLCFoundCallback);
                    List<string> dlcModsInstalled = VanillaDatabaseService.GetInstalledDLCMods(BackupSourceTarget);
                    List<string> installedDLC = VanillaDatabaseService.GetInstalledOfficialDLC(BackupSourceTarget);
                    List<string> allOfficialDLC = MEDirectories.OfficialDLC(BackupSourceTarget.Game);

                    bool end = false;

                    if (installedDLC.Count() < allOfficialDLC.Count())
                    {
                        var dlcList = string.Join("\n - ", allOfficialDLC.Except(installedDLC).Select(x => $@"{MEDirectories.OfficialDLCNames(BackupSourceTarget.Game)[x]} ({x})")); //do not localize
                        dlcList = @" - " + dlcList;
                        Application.Current.Dispatcher.Invoke(delegate
                        {
                            var cancelDueToNotAllDLC = M3L.ShowDialog(window, M3L.GetString(M3L.string_dialog_notAllDLCInstalled, dlcList), M3L.GetString(M3L.string_someDlcNotInstalled), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                            if (cancelDueToNotAllDLC == MessageBoxResult.No)
                            {
                                end = true;
                                EndBackup();
                                return;
                            }
                        });
                    }

                    if (!end)
                    {
                        if (isVanilla && isDLCConsistent && dlcModsInstalled.Count == 0)
                        {
                            BackupStatus = M3L.GetString(M3L.string_waitingForUserInput);

                            string backupPath = null;
                            Application.Current.Dispatcher.Invoke(delegate
                            {
                                Log.Information(@"Prompting user to select backup destination");

                                CommonOpenFileDialog m = new CommonOpenFileDialog
                                {
                                    IsFolderPicker = true,
                                    EnsurePathExists = true,
                                    Title = M3L.GetString(M3L.string_selectBackupDestination)
                                };
                                if (m.ShowDialog() == CommonFileDialogResult.Ok)
                                {
                                    backupPath = m.FileName;
                                    Log.Information(@"Backup path chosen: " + backupPath);

                                    //Check empty
                                    if (Directory.Exists(backupPath))
                                    {
                                        if (Directory.GetFiles(backupPath).Length > 0 ||
                                            Directory.GetDirectories(backupPath).Length > 0)
                                        {
                                            //Directory not empty
                                            Log.Error(@"Selected backup directory is not empty.");
                                            M3L.ShowDialog(window,
                                                M3L.GetString(M3L.string_directoryIsNotEmptyMustBeEmpty),
                                                M3L.GetString(M3L.string_directoryNotEmpty), MessageBoxButton.OK,
                                                MessageBoxImage.Error);
                                            end = true;
                                            EndBackup();
                                            return;
                                        }
                                    }

                                    //Check is Documents folder
                                    var docsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"BioWare", Utilities.GetGameName(Game));
                                    if (backupPath.Contains(docsPath, StringComparison.InvariantCultureIgnoreCase))
                                    {
                                        Log.Error(@"User chose path in or around the documents path for the game- not allowed as game can load files from here.");
                                        M3L.ShowDialog(window, $"The Documents/BioWare/{Utilities.GetGameName(Game)} directory (and it's descendants) cannot be used as a backup location, as the game can load files from it. Please choose another directory.",
                                            "Location not allowed for backup", MessageBoxButton.OK,
                                            MessageBoxImage.Error);
                                        end = true;
                                        EndBackup();
                                        return;
                                    }
                                    
                                    //Check is in a game directory
                                    foreach (var t in window.InstallationTargets)
                                    {
                                        if (t.TargetPath.StartsWith(backupPath, StringComparison.InvariantCultureIgnoreCase))
                                        {
                                            //Backup is subdirectory of a target. This is not allowed as a restore will wipe out the backup
                                            Log.Error(@"User chose path in a game target, this is not allowed as a restore will wipe out the backup");
                                            M3L.ShowDialog(window, $"A backup cannot be placed as a subdirectory of a game. Restoring a backup would result in the backup being deleted when the game is removed before restoration begins. Please choose another directory.",
                                                "Location not allowed for backup", MessageBoxButton.OK,
                                                MessageBoxImage.Error);
                                            end = true;
                                            EndBackup();
                                            return;
                                        }
                                    }

                                    //Check space
                                    Utilities.GetDiskFreeSpaceEx(backupPath, out var freeBytes, out var totalBytes,
                                        out var totalFreeBytes);
                                    var requiredSpace =
                                        Utilities.GetSizeOfDirectory(BackupSourceTarget.TargetPath) * 1.1; //10% buffer

                                    if (freeBytes < requiredSpace)
                                    {
                                        //Not enough space.
                                        Log.Error(
                                            $@"Not enough disk spcae to create backup at {backupPath}. Required space: {ByteSize.FromBytes(requiredSpace)} Free space: {ByteSize.FromBytes(freeBytes)}");
                                        M3L.ShowDialog(window,
                                            M3L.GetString(M3L.string_dialogInsufficientDiskSpace,
                                                Path.GetPathRoot(backupPath), ByteSize.FromBytes(freeBytes).ToString(),
                                                ByteSize.FromBytes(requiredSpace).ToString()),
                                            M3L.GetString(M3L.string_insufficientDiskSpace), MessageBoxButton.OK,
                                            MessageBoxImage.Error);
                                        end = true;
                                        EndBackup();
                                        return;
                                    }

                                    //Check it is not subdirectory of the game (we might want ot check its not subdir of a target)
                                    foreach (var target in AvailableBackupSources)
                                    {
                                        if (backupPath.IsSubPathOf(target.TargetPath))
                                        {
                                            //Not enough space.
                                            Log.Error(
                                                $@"A backup cannot be created in a subdirectory of a game. {backupPath} is a subdir of {BackupSourceTarget.TargetPath}");
                                            M3L.ShowDialog(window,
                                                M3L.GetString(M3L.string_dialogBackupCannotBeSubdirectoryOfGame,
                                                    backupPath, target.TargetPath),
                                                M3L.GetString(M3L.string_cannotCreateBackup), MessageBoxButton.OK,
                                                MessageBoxImage.Error);
                                            end = true;
                                            EndBackup();
                                            return;
                                        }
                                    }
                                }
                                else
                                {
                                    end = true;
                                    EndBackup();
                                    return;
                                }
                            });
                            if (end)
                            {
                                return;
                            }

                            #region callbacks

                            void fileCopiedCallback()
                            {
                                ProgressValue++;
                            }

                            string dlcFolderpath = MEDirectories.DLCPath(BackupSourceTarget) + '\\';
                            int dlcSubStringLen = dlcFolderpath.Length;

                            bool aboutToCopyCallback(string file)
                            {
                                try
                                {
                                    if (file.Contains(@"\cmmbackup\")) return false; //do not copy cmmbackup files
                                    if (file.StartsWith(dlcFolderpath))
                                    {
                                        //It's a DLC!
                                        string dlcname = file.Substring(dlcSubStringLen);
                                        dlcname = dlcname.Substring(0, dlcname.IndexOf('\\'));
                                        if (MEDirectories.OfficialDLCNames(BackupSourceTarget.Game)
                                            .TryGetValue(dlcname, out var hrName))
                                        {
                                            BackupStatusLine2 = M3L.GetString(M3L.string_interp_backingUpX, hrName);
                                        }
                                        else
                                        {
                                            BackupStatusLine2 = M3L.GetString(M3L.string_interp_backingUpX, dlcname);
                                        }
                                    }
                                    else
                                    {
                                        //It's basegame
                                        if (file.EndsWith(@".bik"))
                                        {
                                            BackupStatusLine2 = M3L.GetString(M3L.string_interp_backingUpX,
                                                M3L.GetString(M3L.string_movies));
                                        }
                                        else if (new FileInfo(file).Length > 52428800)
                                        {
                                            BackupStatusLine2 = M3L.GetString(M3L.string_interp_backingUpX,
                                                Path.GetFileName(file));
                                        }
                                        else
                                        {
                                            BackupStatusLine2 = M3L.GetString(M3L.string_interp_backingUpX,
                                                M3L.GetString(M3L.string_basegame));
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    Crashes.TrackError(e, new Dictionary<string, string>()
                                    {
                                        {@"dlcFolderpath", dlcFolderpath},
                                        {@"dlcSubStringLen", dlcSubStringLen.ToString()},
                                        {@"file", file}
                                    });
                                }

                                return true;
                            }

                            void totalFilesToCopyCallback(int total)
                            {
                                ProgressValue = 0;
                                ProgressIndeterminate = false;
                                ProgressMax = total;
                            }

                            #endregion

                            BackupStatus = M3L.GetString(M3L.string_creatingBackup);

                            Log.Information($@"Backing up {BackupSourceTarget.TargetPath} to {backupPath}");

                            CopyDir.CopyAll_ProgressBar(new DirectoryInfo(BackupSourceTarget.TargetPath),
                                new DirectoryInfo(backupPath),
                                totalItemsToCopyCallback: totalFilesToCopyCallback,
                                aboutToCopyCallback: aboutToCopyCallback,
                                fileCopiedCallback: fileCopiedCallback,
                                ignoredExtensions: new[] { @"*.pdf", @"*.mp3" });
                            switch (Game)
                            {
                                case Mod.MEGame.ME1:
                                case Mod.MEGame.ME2:
                                    Utilities.WriteRegistryKey(App.BACKUP_REGISTRY_KEY, Game + @"VanillaBackupLocation",
                                        backupPath);
                                    break;
                                case Mod.MEGame.ME3:
                                    Utilities.WriteRegistryKey(App.REGISTRY_KEY_ME3CMM, @"VanillaCopyLocation",
                                        backupPath);
                                    break;
                            }
                            Log.Information($@"Writing cmm_vanilla");

                            File.Create(Path.Combine(backupPath, @"cmm_vanilla")).Close();

                            Log.Information($@"Backup completed.");

                            Analytics.TrackEvent(@"Created a backup", new Dictionary<string, string>()
                            {
                                {@"Game", Game.ToString()},
                                {@"Result", @"Success"}
                            });

                            EndBackup();
                            return;
                        }


                        if (!isVanilla)
                        {
                            //Show UI for non vanilla
                            Analytics.TrackEvent(@"Created a backup", new Dictionary<string, string>()
                            {
                                {@"Game", Game.ToString()},
                                {@"Result", @"Failure, Game modified"}
                            });
                            b.Result = (nonVanillaFiles, M3L.GetString(M3L.string_cannotBackupModifiedGame),
                                M3L.GetString(M3L.string_followingFilesDoNotMatchTheVanillaDatabase));
                        }
                        else if (!isDLCConsistent)
                        {
                            Analytics.TrackEvent(@"Created a backup", new Dictionary<string, string>()
                            {
                                {@"Game", Game.ToString()},
                                {@"Result", @"Failure, DLC inconsistent"}
                            });
                            if (BackupSourceTarget.Supported)
                            {
                                b.Result = (inconsistentDLC, M3L.GetString(M3L.string_inconsistentDLCDetected),
                                    M3L.GetString(M3L.string_dialogTheFollowingDLCAreInAnInconsistentState));
                            }
                            else
                            {
                                b.Result = (M3L.GetString(M3L.string_inconsistentDLCDetected),
                                    M3L.GetString(M3L.string_inconsistentDLCDetectedUnofficialGame));
                            }
                        }
                        else if (dlcModsInstalled.Count > 0)
                        {
                            Analytics.TrackEvent(@"Created a backup", new Dictionary<string, string>()
                            {
                                {@"Game", Game.ToString()},
                                {@"Result", @"Failure, DLC mods found"}
                            });
                            b.Result = (dlcModsInstalled, M3L.GetString(M3L.string_dlcModsAreInstalled),
                                M3L.GetString(M3L.string_dialogDLCModsWereDetectedCannotBackup));
                        }

                        EndBackup();
                    }
                };
                bw.RunWorkerCompleted += (a, b) =>
                    {

                        if (b.Result is (List<string> listItems, string title, string text))
                        {
                            ListDialog ld = new ListDialog(listItems, title, text, window);
                            ld.Show();
                        }
                        else if (b.Result is (string errortitle, string message))
                        {
                            M3L.ShowDialog(window, message, errortitle, MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        CommandManager.InvalidateRequerySuggested();
                    };
                bw.RunWorkerAsync();
            }

            private void EndBackup()
            {
                Log.Information($@"EndBackup()");
                ResetBackupStatus();
                ProgressIndeterminate = false;
                ProgressVisible = false;
                BackupInProgress = false;
                return;
            }

            private void ResetBackupStatus()
            {
                BackupLocation = Utilities.GetGameBackupPath(Game);
                BackupStatus = BackupLocation != null ? M3L.GetString(M3L.string_backedUp) : M3L.GetString(M3L.string_notBackedUp);
                BackupStatusLine2 = BackupLocation;
            }

            public string GameIconSource { get; }
            public string GameTitle { get; }
            public event PropertyChangedEventHandler PropertyChanged;
            public GameTarget BackupSourceTarget { get; set; }
            public string BackupLocation { get; set; }
            public string BackupStatus { get; set; }
            public string BackupStatusLine2 { get; set; }
            public int ProgressMax { get; set; } = 100;
            public int ProgressValue { get; set; } = 0;
            public bool ProgressIndeterminate { get; set; } = true;
            public bool ProgressVisible { get; set; } = false;
            public ICommand BackupButtonCommand { get; set; }
            public bool BackupOptionsVisible => BackupLocation == null;
            public bool BackupInProgress { get; set; }

        }
    }
}
