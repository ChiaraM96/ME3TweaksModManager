﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using MassEffectModManagerCore.GameDirectories;
using MassEffectModManagerCore.modmanager.helpers;
using MassEffectModManagerCore.modmanager.localizations;
using MassEffectModManagerCore.modmanager.objects;
using MassEffectModManagerCore.modmanager.windows;
using MassEffectModManagerCore.ui;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.WindowsAPICodePack.Dialogs;
using Serilog;


namespace MassEffectModManagerCore.modmanager.usercontrols
{
    /// <summary>
    /// Interaction logic for RestorePanel.xaml
    /// </summary>
    public partial class RestorePanel : MMBusyPanelBase
    {

        public bool AnyGameMissingBackup => !BackupService.ME1BackedUp || !BackupService.ME2BackedUp || !BackupService.ME3BackedUp;
        public ObservableCollectionExtended<GameRestoreObject> GameRestoreControllers { get; } = new ObservableCollectionExtended<GameRestoreObject>();
        private List<GameTarget> targetsList;

        public RestorePanel(List<GameTarget> targetsList, GameTarget selectedTarget)
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
            CloseCommand = new GenericCommand(ClosePanel, CanClose);
        }


        private void ClosePanel()
        {
            OnClosing(new DataEventArgs(GameRestoreControllers.Any(x => x.RefreshTargets)));
        }

        private bool CanClose() => !GameRestoreControllers.Any(x => x.RestoreInProgress);

        public override void HandleKeyPress(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && CanClose())
            {
                ClosePanel();
            }
        }

        public override void OnPanelVisible()
        {
            GameRestoreControllers.Add(new GameRestoreObject(Mod.MEGame.ME1, targetsList.Where(x => x.Game == Mod.MEGame.ME1), window));
            GameRestoreControllers.Add(new GameRestoreObject(Mod.MEGame.ME2, targetsList.Where(x => x.Game == Mod.MEGame.ME2), window));
            GameRestoreControllers.Add(new GameRestoreObject(Mod.MEGame.ME3, targetsList.Where(x => x.Game == Mod.MEGame.ME3), window));
        }

        public class GameRestoreObject : INotifyPropertyChanged
        {
            public bool RefreshTargets;

            private Mod.MEGame Game;
            public ObservableCollectionExtended<GameTarget> AvailableBackupSources { get; } = new ObservableCollectionExtended<GameTarget>();
            private Window window;

            public GameRestoreObject(Mod.MEGame game, IEnumerable<GameTarget> availableBackupSources, Window window)
            {
                this.window = window;
                this.Game = game;
                this.AvailableBackupSources.AddRange(availableBackupSources);
                AvailableBackupSources.Add(new GameTarget(Game, M3L.GetString(M3L.string_restoreToCustomLocation), false, true));
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

                ResetRestoreStatus();
            }

            private void LoadCommands()
            {
                RestoreButtonCommand = new GenericCommand(BeginRestore, CanBeginRestore);
            }

            private bool CanBeginRestore()
            {
                return RestoreTarget != null && !RestoreInProgress && BackupLocation != null;
            }

            public enum RestoreResult
            {
                ERROR_COULD_NOT_DELETE_GAME_DIRECTORY,
                EXCEPTION_DELETING_GAME_DIRECTORY,
                RESTORE_OK,
                ERROR_COULD_NOT_CREATE_DIRECTORY
            }

            private void BeginRestore()
            {
                if (Utilities.IsGameRunning(Game))
                {
                    M3L.ShowDialog(window, M3L.GetString(M3L.string_interp_dialogCannotRestoreXWhileItIsRunning, Utilities.GetGameName(Game)), M3L.GetString(M3L.string_gameRunning), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var result = M3L.ShowDialog(window, M3L.GetString(M3L.string_dialog_restoringXWillDeleteGameDir, Utilities.GetGameName(Game)), M3L.GetString(M3L.string_gameTargetWillBeDeleted), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    NamedBackgroundWorker bw = new NamedBackgroundWorker(Game + @"-Restore");
                    bw.DoWork += (a, b) =>
                    {
                        RestoreInProgress = true;


                        string restoreTargetPath = b.Argument as string;
                        string backupPath = BackupLocation;
                        BackupStatusLine2 = M3L.GetString(M3L.string_deletingExistingGameInstallation);
                        if (Directory.Exists(restoreTargetPath))
                        {
                            if (Directory.GetFiles(restoreTargetPath).Any() || Directory.GetDirectories(restoreTargetPath).Any())
                            {
                                Log.Information(@"Deleting existing game directory: " + restoreTargetPath);
                                try
                                {
                                    bool deletedDirectory = Utilities.DeleteFilesAndFoldersRecursively(restoreTargetPath);
                                    if (deletedDirectory != true)
                                    {
                                        b.Result = RestoreResult.ERROR_COULD_NOT_DELETE_GAME_DIRECTORY;
                                        return;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    //todo: handle this better
                                    Log.Error($@"Exception deleting game directory: {restoreTargetPath}: {ex.Message}");
                                    b.Result = RestoreResult.EXCEPTION_DELETING_GAME_DIRECTORY;
                                    return;
                                }
                            }
                        }
                        else
                        {
                            Log.Error(@"Game directory not found! Was it removed while the app was running?");
                        }

                        //Todo: Revert LODs, remove IndirectSound settings (MEUITM)

                        var created = Utilities.CreateDirectoryWithWritePermission(restoreTargetPath);
                        if (!created)
                        {
                            b.Result = RestoreResult.ERROR_COULD_NOT_CREATE_DIRECTORY;
                            return;
                        }

                        BackupStatusLine2 = M3L.GetString(M3L.string_restoringGameFromBackup);
                        if (restoreTargetPath != null)
                        {
                            //callbacks

                            #region callbacks

                            void fileCopiedCallback()
                            {
                                ProgressValue++;
                            }

                            string dlcFolderpath = MEDirectories.DLCPath(backupPath, Game) + '\\'; //\ at end makes sure we are restoring a subdir
                            int dlcSubStringLen = dlcFolderpath.Length;
                            Debug.WriteLine(@"DLC Folder: " + dlcFolderpath);
                            Debug.Write(@"DLC Fodler path len:" + dlcFolderpath);

                            bool aboutToCopyCallback(string fileBeingCopied)
                            {
                                if (fileBeingCopied.Contains(@"\cmmbackup\")) return false; //do not copy cmmbackup files
                                Debug.WriteLine(fileBeingCopied);
                                if (fileBeingCopied.StartsWith(dlcFolderpath, StringComparison.InvariantCultureIgnoreCase))
                                {
                                    //It's a DLC!
                                    string dlcname = fileBeingCopied.Substring(dlcSubStringLen);
                                    int index = dlcname.IndexOf('\\');
                                    try
                                    {
                                        dlcname = dlcname.Substring(0, index);
                                        if (MEDirectories.OfficialDLCNames(RestoreTarget.Game).TryGetValue(dlcname, out var hrName))
                                        {
                                            BackupStatusLine2 = M3L.GetString(M3L.string_interp_restoringX, hrName);
                                        }
                                        else
                                        {
                                            BackupStatusLine2 = M3L.GetString(M3L.string_interp_restoringX, dlcname);
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        Crashes.TrackError(e, new Dictionary<string, string>()
                                        {
                                            {@"Source", @"Restore UI display callback"},
                                            {@"Value", fileBeingCopied},
                                            {@"DLC Folder path", dlcFolderpath}
                                        });
                                    }
                                }
                                else
                                {
                                    //It's basegame
                                    if (fileBeingCopied.EndsWith(@".bik"))
                                    {
                                        BackupStatusLine2 = M3L.GetString(M3L.string_restoringMovies);
                                    }
                                    else if (new FileInfo(fileBeingCopied).Length > 52428800)
                                    {
                                        BackupStatusLine2 = M3L.GetString(M3L.string_interp_restoringX, Path.GetFileName(fileBeingCopied));
                                    }
                                    else
                                    {
                                        BackupStatusLine2 = M3L.GetString(M3L.string_restoringBasegame);
                                    }
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

                            BackupStatus = M3L.GetString(M3L.string_restoringGame);
                            Log.Information($@"Copying backup to game directory: {backupPath} -> {restoreTargetPath}");
                            CopyDir.CopyAll_ProgressBar(new DirectoryInfo(backupPath), new DirectoryInfo(restoreTargetPath),
                                totalItemsToCopyCallback: totalFilesToCopyCallback,
                                aboutToCopyCallback: aboutToCopyCallback,
                                fileCopiedCallback: fileCopiedCallback,
                                ignoredExtensions: new[] { @"*.pdf", @"*.mp3" });
                            Log.Information(@"Restore of game data has completed");
                        }

                        //Check for cmmvanilla file and remove it present

                        string cmmVanilla = Path.Combine(restoreTargetPath, @"cmm_vanilla");
                        if (File.Exists(cmmVanilla))
                        {
                            Log.Information(@"Removing cmm_vanilla file");
                            File.Delete(cmmVanilla);
                        }

                        Log.Information(@"Restore thread wrapping up");
                        RestoreTarget.ReloadGameTarget(); 
                        b.Result = RestoreResult.RESTORE_OK;
                    };
                    bw.RunWorkerCompleted += (a, b) =>
                    {
                        if (b.Result is RestoreResult result)
                        {
                            switch (result)
                            {
                                case RestoreResult.ERROR_COULD_NOT_CREATE_DIRECTORY:
                                    Analytics.TrackEvent(@"Restored game", new Dictionary<string, string>()
                                    {
                                        {@"Game", Game.ToString()},
                                        {@"Result", @"Failure, Could not create target directory"}
                                    });
                                    M3L.ShowDialog(window, M3L.GetString(M3L.string_dialogCouldNotCreateGameDirectoryAfterDeletion), M3L.GetString(M3L.string_errorRestoringGame), MessageBoxButton.OK, MessageBoxImage.Error);
                                    break;
                                case RestoreResult.ERROR_COULD_NOT_DELETE_GAME_DIRECTORY:
                                    Analytics.TrackEvent(@"Restored game", new Dictionary<string, string>()
                                    {
                                        {@"Game", Game.ToString()},
                                        {@"Result", @"Failure, Could not delete existing game directory"}
                                    });
                                    M3L.ShowDialog(window, M3L.GetString(M3L.string_dialogcouldNotFullyDeleteGameDirectory), M3L.GetString(M3L.string_errorRestoringGame), MessageBoxButton.OK, MessageBoxImage.Error);
                                    break;
                                case RestoreResult.EXCEPTION_DELETING_GAME_DIRECTORY:
                                    Analytics.TrackEvent(@"Restored game", new Dictionary<string, string>()
                                    {
                                        {@"Game", Game.ToString()},
                                        {@"Result", @"Failure, Exception deleting existing game directory"}
                                    });
                                    M3L.ShowDialog(window, M3L.GetString(M3L.string_dialogErrorOccuredDeletingGameDirectory), M3L.GetString(M3L.string_errorRestoringGame), MessageBoxButton.OK, MessageBoxImage.Error);
                                    break;
                                case RestoreResult.RESTORE_OK:
                                    Analytics.TrackEvent(@"Restored game", new Dictionary<string, string>()
                                    {
                                        {@"Game", Game.ToString()},
                                        {@"Result", @"Success"}
                                    });
                                    break;
                            }
                        }

                        EndRestore();
                        CommandManager.InvalidateRequerySuggested();
                    };
                    var restTarget = RestoreTarget.TargetPath;
                    if (RestoreTarget.IsCustomOption)
                    {
                        CommonOpenFileDialog m = new CommonOpenFileDialog
                        {
                            IsFolderPicker = true,
                            EnsurePathExists = true,
                            Title = M3L.GetString(M3L.string_selectNewRestoreDestination)
                        };
                        if (m.ShowDialog() == CommonFileDialogResult.Ok)
                        {
                            //Check empty
                            restTarget = m.FileName;
                            if (Directory.Exists(restTarget))
                            {
                                if (Directory.GetFiles(restTarget).Length > 0 || Directory.GetDirectories(restTarget).Length > 0)
                                {
                                    //Directory not empty
                                    M3L.ShowDialog(window, M3L.GetString(M3L.string_dialogDirectoryIsNotEmptyLocationToRestoreToMustBeEmpty), M3L.GetString(M3L.string_cannotRestoreToThisLocation), MessageBoxButton.OK, MessageBoxImage.Error);
                                    return;
                                }

                                //TODO: PREVENT RESTORING TO DOCUMENTS/BIOWARE
                            }

                            Analytics.TrackEvent(@"Chose to restore game to custom location", new Dictionary<string, string>() { { @"Game", Game.ToString() } });

                        }
                        else
                        {
                            return;
                        }
                    }

                    RefreshTargets = true;
                    bw.RunWorkerAsync(restTarget);
                }
            }

            private void EndRestore()
            {
                ResetRestoreStatus();
                ProgressIndeterminate = false;
                ProgressVisible = false;
                RestoreInProgress = false;
                return;
            }

            private void ResetRestoreStatus()
            {
                BackupLocation = Utilities.GetGameBackupPath(Game);
                BackupStatus = BackupLocation != null ? M3L.GetString(M3L.string_backedUp) : M3L.GetString(M3L.string_notBackedUp);
                BackupStatusLine2 = BackupLocation;
            }

            public string GameIconSource { get; }
            public string GameTitle { get; }
            public event PropertyChangedEventHandler PropertyChanged;
            public GameTarget RestoreTarget { get; set; }
            public string BackupLocation { get; set; }
            public string BackupStatus { get; set; }
            public string BackupStatusLine2 { get; set; }
            public int ProgressMax { get; set; } = 100;
            public int ProgressValue { get; set; } = 0;
            public bool ProgressIndeterminate { get; set; } = true;
            public bool ProgressVisible { get; set; } = false;
            public ICommand RestoreButtonCommand { get; set; }
            public bool BackupOptionsVisible => BackupLocation == null;
            public bool RestoreInProgress { get; set; }

            public string RestoreButtonText
            {
                get
                {
                    if (RestoreTarget != null && BackupLocation != null) return M3L.GetString(M3L.string_restoreThisTarget);
                    if (RestoreTarget == null && BackupLocation != null) return M3L.GetString(M3L.string_selectTarget);
                    if (BackupLocation == null) return M3L.GetString(M3L.string_noBackup);
                    return M3L.GetString(M3L.string_error);
                }
            }
        }
    }
}
