﻿using ATI.ADL;
using LgTv;
using Newtonsoft.Json;
using NLog;
using NStandard;
using NvAPIWrapper.Display;
using NvAPIWrapper.Native.Display;
using NWin32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ColorControl
{
    public partial class MainForm : Form
    {
        private static bool SystemShutdown = false;
        private static bool EndSession = false;
        private static bool UserExit = false;
        private static int SHORTCUTID_SCREENSAVER = -100;
        private static int SHORTCUTID_GAMEBAR = -101;

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private string _dataDir;

        private NvService _nvService;
        private string _lastDisplayRefreshRates = string.Empty;

        private NotifyIcon _trayIcon;
        private bool _initialized = false;
        private bool _disableEvents = false;
        private Config _config;
        private bool _setVisibleCalled = false;
        private RestartDetector _restartDetector;

        private LgService _lgService;

        private ToolStripMenuItem _nvTrayMenu;
        private ToolStripMenuItem _amdTrayMenu;
        private ToolStripMenuItem _lgTrayMenu;

        private StartUpParams StartUpParams { get; }

        private AmdService _amdService;
        private bool _skipResize;
        private FileVersionInfo _currentVersionInfo;
        private bool _checkedForUpdates = false;
        private string _updateHtmlUrl;

        private LgGameBar _gameBarForm;

        public MainForm(AppContext appContext)
        {
            InitializeComponent();
            StartUpParams = appContext.StartUpParams;

            _dataDir = Program.DataDir;
            _config = Program.Config;

            LoadConfig();

            MessageForms.MainForm = this;

            _nvTrayMenu = new ToolStripMenuItem("NVIDIA presets");
            _amdTrayMenu = new ToolStripMenuItem("NVIDIA presets");
            _lgTrayMenu = new ToolStripMenuItem("LG presets");
            _trayIcon = new NotifyIcon()
            {
                Icon = Icon,
                ContextMenuStrip = new ContextMenuStrip(),
                Visible = _config.MinimizeToTray,
                Text = Text
            };

            _trayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[] {
                    _nvTrayMenu,
                    _amdTrayMenu,
                    _lgTrayMenu,
                    new ToolStripSeparator(),
                    new ToolStripMenuItem("Open", null, OpenForm),
                    new ToolStripSeparator(),
                    new ToolStripMenuItem("Exit", null, Exit)
                });

            _trayIcon.MouseDoubleClick += trayIcon_MouseDoubleClick;
            _trayIcon.ContextMenuStrip.Opened += trayIconContextMenu_Popup;
            _trayIcon.BalloonTipClicked += trayIconBalloonTip_Clicked;

            chkStartAfterLogin.Checked = Utils.TaskExists(Program.TS_TASKNAME, true);

            chkFixChromeFonts.Enabled = Utils.IsChromeInstalled();
            if (chkFixChromeFonts.Enabled)
            {
                var fixInstalled = Utils.IsChromeFixInstalled();
                if (_config.FixChromeFonts && !fixInstalled)
                {
                    Utils.ExecuteElevated(StartUpParams.ActivateChromeFontFixParam);
                }
                chkFixChromeFonts.Checked = Utils.IsChromeFixInstalled();
            }

            InitNvService();
            InitAmdService();
            InitLgService();

            InitInfo();

            UserSessionInfo.Install();

            _restartDetector = new RestartDetector();

            //Scale(new SizeF(1.25F, 1.25F));

            _initialized = true;

            AfterInitialized();
        }

        private void InitNvService()
        {
            try
            {
                //throw new Exception("bla");
                _nvService = new NvService(_dataDir);
                FillNvPresets();

                _nvService.AfterApplyPreset += NvServiceAfterApplyPreset;
            }
            catch (Exception ex)
            {
                //Logger.Error("Error initializing NvService: " + e.ToLogString());
                Logger.Debug($"No NVIDIA device detected: {ex.Message}");
                tcMain.TabPages.Remove(tabNVIDIA);
            }
        }

        private void NvServiceAfterApplyPreset(object sender, NvPreset preset)
        {
            UpdateDisplayInfoItems();
        }

        private void InitAmdService()
        {
            try
            {
                _amdService = new AmdService(_dataDir);
                FillAmdPresets();

                _amdService.AfterApplyPreset += AmdServiceAfterApplyPreset;
            }
            catch (Exception)
            {
                //Logger.Error("Error initializing AmdService: " + e.ToLogString());
                Logger.Debug("No AMD device detected");
                tcMain.TabPages.Remove(tabAMD);
            }
        }

        private void AmdServiceAfterApplyPreset(object sender, AmdPreset preset)
        {
            UpdateDisplayInfoItemsAmd();
        }

        private void InitLgService()
        {
            try
            {
                _lgService = new LgService(_dataDir, StartUpParams.RunningFromScheduledTask);
                _lgService.RefreshDevices(afterStartUp: true).ContinueWith((_) => BeginInvoke(new Action(AfterLgServiceRefreshDevices)));

                FillLgPresets();

                edtLgMaxPowerOnRetries.Value = _lgService.Config.PowerOnRetries;
                edtLgDeviceFilter.Text = _lgService.Config.DeviceSearchKey;
                chkLgShowAdvancedActions.Checked = _lgService.Config.ShowAdvancedActions;

                var values = Enum.GetValues(typeof(ButtonType));
                foreach (var button in values)
                {
                    var text = button.ToString();
                    if (text[0] == '_')
                    {
                        text = text.Substring(1);
                    }

                    var item = mnuLgRcButtons.DropDownItems.Add(text);
                    item.Click += miLgAddButton_Click;
                }

                _lgService.InstallEventHandlers();

                LgDevice.ExternalServiceHandler = HandleExternalServiceForLgDevice;

                if (!string.IsNullOrEmpty(_lgService.Config.GameBarShortcut))
                {
                    Utils.RegisterShortcut(Handle, SHORTCUTID_GAMEBAR, _lgService.Config.GameBarShortcut);

                    edtLgGameBarShortcut.Text = _lgService.Config.GameBarShortcut;
                }
            }
            catch (Exception e)
            {
                Logger.Error("Error initializing LgService: " + e.ToLogString());
            }
        }

        private void AfterLgServiceRefreshDevices()
        {
            FillLgDevices();

            if (StartUpParams.ExecuteLgPreset)
            {
                var _ = _lgService.ApplyPreset(StartUpParams.LgPresetName);
            }
        }

        private bool HandleExternalServiceForLgDevice(string serviceName, string[] parameters)
        {
            if (string.IsNullOrEmpty(serviceName) || parameters.Length == 0)
            {
                return false;
            }

            if (_nvService != null && serviceName.Equals("NvPreset", StringComparison.OrdinalIgnoreCase))
            {
                return _nvService.ApplyPreset(parameters[0]);
            }
            if (_amdService != null && serviceName.Equals("AmdPreset", StringComparison.OrdinalIgnoreCase))
            {
                return _amdService.ApplyPreset(parameters[0]);
            }

            return false;
        }

        private void InitInfo()
        {
            _currentVersionInfo = FileVersionInfo.GetVersionInfo(Path.GetFileName(Application.ExecutablePath));

            //if (ApplicationDeployment.IsNetworkDeployed)
            //{
            //    Text = Application.ProductName + " " + ApplicationDeployment.CurrentDeployment.CurrentVersion.ToString();
            //}
            //else
            //{
                Text = Application.ProductName + " " + Application.ProductVersion;
            //}

            lblInfo.Text = Text + " - " + _currentVersionInfo.LegalCopyright;

            lbPlugins.Items.Add("lgtv.net by gr4b4z");
            lbPlugins.Items.Add("Newtonsoft.Json by James Newton-King");
            lbPlugins.Items.Add("NLog by Jarek Kowalski, Kim Christensen, Julian Verdurmen");
            lbPlugins.Items.Add("NvAPIWrapper.Net by Soroush Falahati");
            lbPlugins.Items.Add("NWin32 by zmjack");
            lbPlugins.Items.Add("TaskScheduler by David Hall");
        }

        private void OpenForm(object sender, EventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void Exit(object sender, EventArgs e)
        {
            UserExit = true;
            Close();
        }

        private void trayIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
        }

        private void trayIconBalloonTip_Clicked(object sender, EventArgs e)
        {
            if (_updateHtmlUrl != null)
            {
                Utils.StartProcess(_updateHtmlUrl);
            }
        }

        private void FillNvPresets()
        {
            Utils.InitListView(lvNvPresets, NvPreset.GetColumnNames());

            UpdateDisplayInfoItems();

            foreach (var preset in _nvService.GetPresets())
            {
                AddOrUpdateItem(preset);
                Utils.RegisterShortcut(Handle, preset.id, preset.shortcut);
            }
        }

        private void FillAmdPresets()
        {
            Utils.InitListView(lvAmdPresets, AmdPreset.GetColumnNames());

            UpdateDisplayInfoItemsAmd();

            foreach (var preset in _amdService.GetPresets())
            {
                AddOrUpdateItemAmd(preset);
                Utils.RegisterShortcut(Handle, preset.id, preset.shortcut);
            }
        }

        private void UpdateDisplayInfoItems()
        {
            var displays = _nvService?.GetDisplayInfos();
            if (displays == null)
            {
                return;
            }

            var text = Program.TS_TASKNAME;
            foreach (var displayInfo in displays)
            {
                var display = displayInfo.Display;

                var id = Math.Abs((int)display.Handle.MemoryAddress.ToInt64());

                ListViewItem item = null;
                for (var i = 0; i < lvNvPresets.Items.Count; i++)
                {
                    item = lvNvPresets.Items[i];

                    if (item.Tag == null && item.ImageIndex == id)
                    {
                        break;
                    }
                    item = null;
                }

                if (item == null)
                {
                    item = lvNvPresets.Items.Add(display.Name);
                    item.ImageIndex = id;
                    item.Font = new Font(item.Font, item.Font.Style | FontStyle.Bold);
                    item.BackColor = Color.LightGray;
                }

                var values = displayInfo.Values;

                item.Text = values[0];
                for (var i = 1; i < values.Count; i++)
                {
                    if (item.SubItems.Count - 1 >= i)
                    {
                        item.SubItems[i].Text = values[i];
                    }
                    else
                    {
                        item.SubItems.Add(values[i]);
                    }
                }

                text += "\n" + displayInfo.InfoLine;
            }

            Utils.SetNotifyIconText(_trayIcon, text);
        }

        private void UpdateDisplayInfoItemsAmd()
        {
            var displays = _amdService?.GetDisplayInfos();
            if (displays == null)
            {
                return;
            }

            var text = Program.TS_TASKNAME;
            foreach (var displayInfo in displays)
            {
                var display = displayInfo.Display;

                var id = display.DisplayID.DisplayPhysicalIndex;

                ListViewItem item = null;
                for (var i = 0; i < lvAmdPresets.Items.Count; i++)
                {
                    item = lvAmdPresets.Items[i];

                    if (item.Tag == null && item.ImageIndex == id)
                    {
                        break;
                    }
                    item = null;
                }

                if (item == null)
                {
                    item = lvAmdPresets.Items.Add(display.DisplayName);
                    item.ImageIndex = id;
                    item.Font = new Font(item.Font, item.Font.Style | FontStyle.Bold);
                    item.BackColor = Color.LightGray;
                }

                var values = displayInfo.Values;

                item.Text = values[0];
                for (var i = 1; i < values.Count; i++)
                {
                    if (item.SubItems.Count - 1 >= i)
                    {
                        item.SubItems[i].Text = values[i];
                    }
                    else
                    {
                        item.SubItems.Add(values[i]);
                    }
                }

                text += "\n" + displayInfo.InfoLine;
            }

            Utils.SetNotifyIconText(_trayIcon, text);
        }

        private void AddOrUpdateItem(NvPreset preset = null)
        {
            Utils.AddOrUpdateListItem(lvNvPresets, _nvService.GetPresets(), _config, preset);
        }

        private void AddOrUpdateItemAmd(AmdPreset preset = null)
        {
            Utils.AddOrUpdateListItem(lvAmdPresets, _amdService.GetPresets(), _config, preset);
        }

        private void btnApply_Click(object sender, EventArgs e)
        {
            ApplySelectedNvPreset();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!(SystemShutdown || EndSession || UserExit) && _config.MinimizeOnClose)
            {
                e.Cancel = true;
                WindowState = FormWindowState.Minimized;
                UserExit = false;
                return;
            }

            GlobalSave();

            if (SystemShutdown && _lgService.Devices.Any(d => d.PowerOffOnShutdown))
            {
                Logger.Debug($"MainForm_FormClosing: SystemShutdown");

                if (_restartDetector != null && (_restartDetector.RestartDetected || _restartDetector.IsRebootInProgress()))
                {
                    Logger.Debug("Not powering off because of a restart");
                }
                else if (NativeMethods.GetAsyncKeyState(NativeConstants.VK_CONTROL) < 0 || NativeMethods.GetAsyncKeyState(NativeConstants.VK_RCONTROL) < 0)
                {
                    Logger.Debug("Not powering off because CONTROL-key is down");
                }
                else
                {
                    Logger.Debug("Powering off tv...");
                    var task = _lgService.PowerOffOnShutdownOrResume();
                    Utils.WaitForTask(task);
                    Logger.Debug("Done powering off tv");
                    //ExecPowerOffPreset(true);
                }
            }
        }

        private void GlobalSave()
        {
            _nvService?.GlobalSave();
            _amdService?.GlobalSave();
            _lgService?.GlobalSave();

            SaveConfig();
        }

        private void lvNvPresets_SelectedIndexChanged(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();
            var enabled = preset != null;

            btnApply.Enabled = enabled;
            btnChange.Enabled = enabled;
            edtNvPresetName.Enabled = enabled;
            edtShortcut.Enabled = enabled;
            btnNvPresetSave.Enabled = enabled;
            btnClone.Enabled = enabled;
            btnNvPresetDelete.Enabled = enabled;

            if (preset != null)
            {
                edtNvPresetName.Text = preset.name;
                edtShortcut.Text = preset.shortcut;
            }
            else
            {
                edtNvPresetName.Text = string.Empty;
                edtShortcut.Text = string.Empty;
            }
        }

        private Control FindFocusedControl()
        {
            ContainerControl container = this;
            Control control = null;
            while (container != null)
            {
                control = container.ActiveControl;
                container = control as ContainerControl;
            }
            
            return control;
        }

        private bool IsShortcutControlFocused()
        {
            var control = FindFocusedControl();
            return control?.Name.Contains("Shortcut") ?? false;
        }

        protected override void WndProc(ref Message m)
        {
            // 5. Catch when a HotKey is pressed !
            if (m.Msg == NativeConstants.WM_HOTKEY && !IsShortcutControlFocused())
            {
                int id = m.WParam.ToInt32();

                // 6. Handle what will happen once a respective hotkey is pressed
                if (id == SHORTCUTID_SCREENSAVER)
                {
                    var screenSaver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "scrnsave.scr");
                    Process.Start("explorer.exe", screenSaver);
                }
                else if (id == SHORTCUTID_GAMEBAR)
                {
                    ToggleGameBar();
                }
                else
                {
                    var preset = _nvService?.GetPresets().FirstOrDefault(x => x.id == id);
                    if (preset != null)
                    {
                        ApplyNvPreset(preset);
                    }

                    var amdPreset = _amdService?.GetPresets().FirstOrDefault(x => x.id == id);
                    if (amdPreset != null)
                    {
                        ApplyAmdPreset(amdPreset);
                    }

                    var lgPreset = _lgService?.GetPresets().FirstOrDefault(x => x.id == id);
                    if (lgPreset != null)
                    {
                        ApplyLgPreset(lgPreset);
                    }
                }
            }
            else if (m.Msg == NativeConstants.WM_QUERYENDSESSION)
            {
                SystemShutdown = true;
            }
            else if (m.Msg == NativeConstants.WM_ENDSESSION)
            {
                EndSession = true;
            }
            else if (m.Msg == Utils.WM_BRINGTOFRONT)
            {
                Logger.Debug("WM_BRINGTOFRONT message received, opening form");
                OpenForm(this, EventArgs.Empty);
            }

            base.WndProc(ref m);
        }

        private void edtShortcut_KeyDown(object sender, KeyEventArgs e)
        {
            ((TextBox)sender).Text = Utils.FormatKeyboardShortcut(e);
        }

        private void btnSetShortcut_Click(object sender, EventArgs e)
        {
            var shortcut = edtShortcut.Text.Trim();

            if (!Utils.ValidateShortcut(shortcut))
            {
                return;
            }

            var preset = GetSelectedNvPreset();

            var name = edtNvPresetName.Text.Trim();

            if (!string.IsNullOrEmpty(name) && _nvService.GetPresets().Any(x => x.id != preset.id && x.name != null && x.name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageForms.WarningOk("The name must be unique.");
                return;
            }

            var clear = !string.IsNullOrEmpty(preset.shortcut);

            preset.shortcut = shortcut;
            preset.name = name;

            AddOrUpdateItem();

            Utils.RegisterShortcut(Handle, preset.id, preset.shortcut, clear);
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            // Hide tray icon, otherwise it will remain shown until user mouses over it
            _trayIcon.Visible = false;
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (_skipResize)
            {
                return;
            }

            if (WindowState == FormWindowState.Minimized)
            {
                if (_config.MinimizeToTray)
                {
                    Hide();
                }
            }
            else if (WindowState == FormWindowState.Normal && _config != null)
            {
                _config.FormWidth = Width;
                _config.FormHeight = Height;
            }
        }

        private void chkStartAfterLogin_CheckedChanged(object sender, EventArgs e)
        {
            if (!_initialized)
            {
                return;
            }
            var enabled = chkStartAfterLogin.Checked;
            Utils.ExecuteElevated(enabled ? StartUpParams.EnableAutoStartParam : StartUpParams.DisableAutoStartParam);
        }

        private void LoadConfig()
        {
            chkStartMinimized.Checked = _config.StartMinimized;
            chkMinimizeOnClose.Checked = _config.MinimizeOnClose;
            chkMinimizeToSystemTray.Checked = _config.MinimizeToTray;
            chkCheckForUpdates.Checked = _config.CheckForUpdates;
            edtDelayDisplaySettings.Value = _config.DisplaySettingsDelay;
            edtBlankScreenSaverShortcut.Text = _config.ScreenSaverShortcut;
            chkGdiScaling.Checked = _config.UseGdiScaling;

            if (!string.IsNullOrEmpty(_config.ScreenSaverShortcut))
            {
                Utils.RegisterShortcut(Handle, SHORTCUTID_SCREENSAVER, _config.ScreenSaverShortcut);
            }

            _skipResize = true;
            try
            {
                Width = _config.FormWidth;
                Height = _config.FormHeight;
            }
            finally
            {
                _skipResize = false;
            }
        }

        private void SaveConfig()
        {
            _config.StartMinimized = chkStartMinimized.Checked;
            if (WindowState != FormWindowState.Minimized)
            {
                _config.FormWidth = Width;
                _config.FormHeight = Height;
            }

            try
            {
                var data = JsonConvert.SerializeObject(_config);
                File.WriteAllText(Program.ConfigFilename, data);
            }
            catch (Exception e)
            {
                Logger.Error(e.ToLogString());
            }
        }

        private void ShowControls(Control parent, bool show = true, Control exclude = null)
        {
            for (var i = 0; i < parent.Controls.Count; i++)
            {
                var control = parent.Controls[i];
                if (control != exclude)
                {
                    control.Visible = show;
                }
            }
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            if (_nvService == null)
            {
                ShowControls(tabNVIDIA, false, lblError);

                lblError.Text = "Error while initializing the NVIDIA-wrapper. You either don't have a NVIDIA GPU or it is disabled. NVIDIA controls will not be available.";
                lblError.Visible = true;
            }

            if (_amdService == null)
            {
                ShowControls(tabAMD, false, lblErrorAMD);

                lblErrorAMD.Text = "Error while initializing the ADL-wrapper. You either don't have a AMD GPU or it is disabled. AMD controls will not be available.";
                lblErrorAMD.Visible = true;
            }

            if (_lgService == null)
            {
                ShowControls(tabLG, false, lblLgError);
                lblLgError.Text = "Error while initializing the LG-controller. You either don't have a LG TV or it is disabled.";
                lblLgError.Visible = true;
            }

            InitSelectedTab();
            CheckForUpdates();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!_setVisibleCalled && _config.StartMinimized)
            {
                _setVisibleCalled = true;
                if (_config.MinimizeToTray)
                {
                    value = false;
                }
                else
                {
                    WindowState = FormWindowState.Minimized;
                }
            }
            if (!IsDisposed) {
                base.SetVisibleCore(value);
            }
        }

        private void edtShortcut_KeyUp(object sender, KeyEventArgs e)
        {
            Utils.HandleKeyboardShortcutUp(e);
        }

        private void edtShortcut_TextChanged(object sender, EventArgs e)
        {
            var text = edtShortcut.Text;

            var preset = GetSelectedNvPreset();

            if (preset == null || string.IsNullOrEmpty(text))
            {
                edtShortcut.ForeColor = SystemColors.WindowText;
            }
            else
            {
                edtShortcut.ForeColor = ShortCutExists(text, preset.id) ? Color.Red : SystemColors.WindowText;
            }
        }

        private NvPreset GetSelectedNvPreset()
        {
            if (lvNvPresets.SelectedItems.Count > 0)
            {
                var item = lvNvPresets.SelectedItems[0];
                return (NvPreset)item.Tag;
            }
            else
            {
                return null;
            }
        }

        private AmdPreset GetSelectedAmdPreset()
        {
            if (lvAmdPresets.SelectedItems.Count > 0)
            {
                var item = lvAmdPresets.SelectedItems[0];
                return (AmdPreset)item.Tag;
            }
            else
            {
                return null;
            }
        }

        private void btnClone_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            var newPreset = preset.Clone();
            AddOrUpdateItem(newPreset);
        }

        private void miNvPresetColorSettings_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            preset.applyColorData = !preset.applyColorData;

            AddOrUpdateItem();
        }

        private void miNvPresetApplyDithering_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            preset.applyDithering = !preset.applyDithering;

            AddOrUpdateItem();
        }

        private void miNvPresetDitheringEnabled_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            preset.ditheringEnabled = !preset.ditheringEnabled;

            AddOrUpdateItem();
        }

        private void btnChange_Click(object sender, EventArgs e)
        {
            mnuNvPresets.Show(btnChange, btnChange.PointToClient(Cursor.Position));
        }

        private void includedToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            preset.applyRefreshRate = !preset.applyRefreshRate;

            AddOrUpdateItem();
        }

        private void mnuNvPresets_Opening(object sender, CancelEventArgs e)
        {
            var preset = GetSelectedNvPreset();

            miNvApply.Enabled = preset != null;
            miNvPresetApplyOnStartup.Enabled = preset != null;
            mnuNvDisplay.Enabled = preset != null;
            miNvPresetColorSettings.Enabled = preset != null;
            mnuNvPresetsColorSettings.Enabled = preset != null;
            mnuRefreshRate.Enabled = preset != null;
            miNvPresetDithering.Enabled = preset != null;
            miNvHDR.Enabled = preset != null;

            if (preset != null)
            {
                miNvPresetApplyOnStartup.Checked = _config.NvPresetId_ApplyOnStartup == preset.id;

                if (mnuNvDisplay.DropDownItems.Count == 1)
                {
                    var displays = _nvService.GetDisplays();
                    for (var i = 0; i < displays.Length; i++)
                    {
                        var display = displays[i];
                        var name = display.Name;
                        var screen = Screen.AllScreens.FirstOrDefault(x => x.DeviceName.Equals(name));
                        if (screen != null)
                        {
                            name += " (" + screen.DeviceFriendlyName() + ")";
                        }

                        var item = mnuNvDisplay.DropDownItems.Add(name);
                        item.Tag = display;
                        item.Click += displayMenuItem_Click;
                    }
                }

                Utils.BuildDropDownMenu(mnuNvPresetsColorSettings, "Bit depth", typeof(ColorDataDepth), preset.colorData, "ColorDepth", nvPresetColorDataMenuItem_Click);
                Utils.BuildDropDownMenu(mnuNvPresetsColorSettings, "Format", typeof(ColorDataFormat), preset.colorData, "ColorFormat", nvPresetColorDataMenuItem_Click);
                Utils.BuildDropDownMenu(mnuNvPresetsColorSettings, "Dynamic range", typeof(ColorDataDynamicRange), preset.colorData, "DynamicRange", nvPresetColorDataMenuItem_Click);
                Utils.BuildDropDownMenu(mnuNvPresetsColorSettings, "Color space", typeof(ColorDataColorimetry), preset.colorData, "Colorimetry", nvPresetColorDataMenuItem_Click);

                if (preset.displayName != _lastDisplayRefreshRates)
                {
                    while (mnuRefreshRate.DropDownItems.Count > 1)
                    {
                        mnuRefreshRate.DropDownItems.RemoveAt(mnuRefreshRate.DropDownItems.Count - 1);
                    }
                }

                if (mnuRefreshRate.DropDownItems.Count == 1)
                {
                    var refreshRates = _nvService.GetAvailableRefreshRates(preset);
                    _lastDisplayRefreshRates = preset.displayName;

                    foreach (var refreshRate in refreshRates)
                    {
                        var item = mnuRefreshRate.DropDownItems.Add(refreshRate.ToString() + "Hz");
                        item.Tag = refreshRate;
                        item.Click += refreshRateMenuItem_Click;
                    }
                }

                miNvPrimaryDisplay.Checked = preset.primaryDisplay;
                foreach (var item in mnuNvDisplay.DropDownItems)
                {
                    if (item is ToolStripMenuItem)
                    {
                        var menuItem = (ToolStripMenuItem)item;
                        if (menuItem.Tag != null)
                        {
                            menuItem.Checked = ((Display)menuItem.Tag).Name.Equals(preset.displayName);
                        }
                    }
                }

                miNvPresetColorSettings.Checked = preset.applyColorData;

                miRefreshRateIncluded.Checked = preset.applyRefreshRate;
                foreach (var item in mnuRefreshRate.DropDownItems)
                {
                    if (item is ToolStripMenuItem)
                    {
                        var menuItem = (ToolStripMenuItem)item;
                        if (menuItem.Tag != null)
                        {
                            menuItem.Checked = (uint)menuItem.Tag == preset.refreshRate;
                        }
                    }
                }

                miNvPresetApplyDithering.Checked = preset.applyDithering;
                miNvPresetDitheringEnabled.Checked = preset.ditheringEnabled;

                foreach (var item in mnuNvDitheringBitDepth.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Tag != null)
                    {
                        item.Checked = uint.Parse(item.Tag.ToString()) == preset.ditheringBits;
                    }
                }
                foreach (var item in mnuNvDitheringMode.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Tag != null)
                    {
                        item.Checked = uint.Parse(item.Tag.ToString()) == preset.ditheringMode;
                    }
                }

                miHDRIncluded.Checked = preset.applyHDR;
                miHDREnabled.Checked = preset.HDREnabled;
                miToggleHDR.Checked = preset.toggleHDR;
            }
        }

        private void nvPresetColorDataMenuItem_Click(object sender, EventArgs e)
        {
            var menuItem = (ToolStripMenuItem)sender;
            var property = (PropertyInfo)menuItem.OwnerItem.Tag;

            var value = menuItem.Tag;

            var preset = GetSelectedNvPreset();

            var dictionary = new Dictionary<string, object>
            {
                { "ColorFormat", preset.colorData.ColorFormat },
                { "ColorDepth", preset.colorData.ColorDepth },
                { "Colorimetry", preset.colorData.Colorimetry },
                { "DynamicRange", preset.colorData.DynamicRange },
                { "SelectionPolicy", preset.colorData.SelectionPolicy }
            };

            dictionary[property.Name] = value;

            var colorData = NvPreset.GenerateColorData(dictionary);

            preset.colorData = colorData;

            AddOrUpdateItem();
        }

        private void refreshRateMenuItem_Click(object sender, EventArgs e)
        {
            var refreshRate = (uint)((ToolStripItem)sender).Tag;

            var preset = GetSelectedNvPreset();

            preset.refreshRate = refreshRate;

            AddOrUpdateItem();
        }

        private void refreshRateMenuItemAmd_Click(object sender, EventArgs e)
        {
            var refreshRate = (uint)((ToolStripItem)sender).Tag;

            var preset = GetSelectedAmdPreset();

            preset.refreshRate = refreshRate;

            AddOrUpdateItemAmd();
        }

        private void displayMenuItem_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            var menuItem = (ToolStripItem)sender;
            if (menuItem.Tag != null)
            {
                var display = (Display)menuItem.Tag;

                preset.displayName = display.Name;
                preset.primaryDisplay = false;
            }
            else
            {
                preset.primaryDisplay = true;
                preset.displayName = null;
            }

            AddOrUpdateItem();
        }

        private void displayMenuItemAmd_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            var menuItem = (ToolStripItem)sender;
            if (menuItem.Tag != null)
            {
                var display = (ADLDisplayInfo)menuItem.Tag;

                preset.displayName = display.DisplayName;
                preset.primaryDisplay = false;
            }
            else
            {
                preset.primaryDisplay = true;
                preset.displayName = null;
            }

            AddOrUpdateItemAmd();
        }

        private void miHDRIncluded_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            preset.applyHDR = !preset.applyHDR;

            AddOrUpdateItem();
        }

        private void miToggleHDR_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            preset.toggleHDR = !preset.toggleHDR;

            if (preset.toggleHDR)
            {
                preset.HDREnabled = false;
            }

            AddOrUpdateItem();
        }

        private void miHDREnabled_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            preset.HDREnabled = !preset.HDREnabled;
            preset.toggleHDR = false;

            AddOrUpdateItem();
        }

        private void FillLgPresets()
        {
            Utils.InitListView(lvLgPresets, LgPreset.GetColumnNames());

            foreach (var preset in _lgService.GetPresets())
            {
                AddOrUpdateItemLg(preset);
                Utils.RegisterShortcut(Handle, preset.id, preset.shortcut);
            }
        }

        private void AddOrUpdateItemLg(LgPreset preset = null, ListViewItem specItem = null)
        {
            Utils.AddOrUpdateListItem(lvLgPresets, _lgService.GetPresets(), _config, preset, specItem);
        }

        private void btnCloneLg_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedLgPreset();

            var newPreset = preset.Clone();
            AddOrUpdateItemLg(newPreset);
        }

        private LgPreset GetSelectedLgPreset()
        {
            if (lvLgPresets.SelectedItems.Count > 0)
            {
                var item = lvLgPresets.SelectedItems[0];
                return (LgPreset)item.Tag;
            }
            else
            {
                return null;
            }
        }

        private void lvLgPresets_SelectedIndexChanged(object sender, EventArgs e)
        {
            var enabled = lvLgPresets.SelectedItems.Count > 0;
            btnApplyLg.Enabled = enabled;
            btnCloneLg.Enabled = enabled;
            edtNameLg.Enabled = enabled;
            cbxLgPresetDevice.Enabled = enabled;
            edtShortcutLg.Enabled = enabled;
            btnSetShortcutLg.Enabled = enabled;
            edtStepsLg.Enabled = enabled;
            btnLgAddButton.Enabled = enabled;
            cbxLgApps.Enabled = enabled;
            btnDeleteLg.Enabled = enabled;
            cbxLgPresetTrigger.Enabled = enabled;
            edtLgPresetTriggerConditions.Enabled = enabled;
            btnLgPresetEditTriggerConditions.Enabled = enabled;
            edtLgPresetIncludedProcesses.Enabled = enabled;
            edtLgPresetExcludedProcesses.Enabled = enabled;

            var preset = GetSelectedLgPreset();

            if (preset != null)
            {
                edtNameLg.Text = preset.name;
                var lgApps = _lgService?.GetApps();
                cbxLgApps.SelectedIndex = lgApps == null ? -1 : lgApps.FindIndex(x => x.appId.Equals(preset.appId));
                edtShortcutLg.Text = preset.shortcut;
                edtStepsLg.Text = preset.steps.Aggregate("", (a, b) => (string.IsNullOrEmpty(a) ? "" : a + ", ") + b);

                var index = -1;

                for (var i = 0; i < cbxLgPresetDevice.Items.Count; i++)
                {
                    var pnpDev = (LgDevice)cbxLgPresetDevice.Items[i];
                    if ((string.IsNullOrEmpty(preset.DeviceMacAddress) && string.IsNullOrEmpty(pnpDev.MacAddress)) || pnpDev.MacAddress.Equals(preset.DeviceMacAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                cbxLgPresetDevice.SelectedIndex = index;

                var trigger = preset.Triggers.FirstOrDefault();

                Utils.SetComboBoxEnumIndex(cbxLgPresetTrigger, (int)(trigger?.Trigger ?? PresetTriggerType.None));

                edtLgPresetTriggerConditions.Text = Utils.GetDescriptions<PresetConditionType>(trigger != null ? (int)trigger.Conditions : 0).Join(", ");
                edtLgPresetTriggerConditions.Tag = trigger != null ? (int)trigger.Conditions : 0;

                edtLgPresetIncludedProcesses.Text = trigger?.IncludedProcesses?.Join(", ") ?? string.Empty;
                edtLgPresetExcludedProcesses.Text = trigger?.ExcludedProcesses?.Join(", ") ?? string.Empty;
            }
            else
            {
                edtNameLg.Text = string.Empty;
                cbxLgApps.SelectedIndex = -1;
                edtShortcutLg.Text = string.Empty;
                edtStepsLg.Text = string.Empty;
                cbxLgPresetDevice.SelectedIndex = -1;
                cbxLgPresetTrigger.SelectedIndex = -1;
                edtLgPresetTriggerConditions.Text = string.Empty;
                edtLgPresetTriggerConditions.Tag = 0;
                edtLgPresetIncludedProcesses.Text = string.Empty;
                edtLgPresetExcludedProcesses.Text = string.Empty;
            }
        }

        private void btnApplyLg_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedLgPreset();

            ApplyLgPreset(preset);
        }

        private void btnSetShortcutLg_Click(object sender, EventArgs e)
        {
            var shortcut = edtShortcutLg.Text.Trim();
            if (!Utils.ValidateShortcut(shortcut))
            {
                return;
            }

            var preset = GetSelectedLgPreset();

            var name = edtNameLg.Text.Trim();

            if (name.Length == 0 || _lgService.GetPresets().Any(x => x.id != preset.id && x.name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageForms.WarningOk("The name can not be empty and must be unique.");
                return;
            }

            var clear = !string.IsNullOrEmpty(preset.shortcut);

            preset.name = name;

            if (cbxLgPresetDevice.SelectedIndex == -1)
            {
                preset.DeviceMacAddress = string.Empty;
            }
            else
            {
                var device = (LgDevice)cbxLgPresetDevice.SelectedItem;
                preset.DeviceMacAddress = device.MacAddress;
            }

            if (cbxLgApps.SelectedIndex == -1)
            {
                preset.appId = string.Empty;
            }
            else
            {
                var lgApp = (LgApp)cbxLgApps.SelectedItem;
                preset.appId = lgApp.appId;
            }

            var triggerType = Utils.GetComboBoxEnumItem<PresetTriggerType>(cbxLgPresetTrigger);
            preset.UpdateTrigger(triggerType,
                                 (PresetConditionType)edtLgPresetTriggerConditions.Tag,
                                 edtLgPresetIncludedProcesses.Text,
                                 edtLgPresetExcludedProcesses.Text);

            if (triggerType != PresetTriggerType.None)
            {
                _lgService.InstallEventHandlers();
            }

            var shortcutChanged = !shortcut.Equals(preset.shortcut);
            if (shortcutChanged)
            {
                preset.shortcut = shortcut;
            }

            var text = edtStepsLg.Text;

            Utils.ParseWords(preset.steps, text);

            AddOrUpdateItemLg();

            if (shortcutChanged)
            {
                Utils.RegisterShortcut(Handle, preset.id, preset.shortcut, clear);
            }
        }

        private void btnNvPresetDelete_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            if (preset == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(preset.shortcut))
            {
                Utils.UnregisterHotKey(Handle, preset.id);
            }

            _nvService.GetPresets().Remove(preset);

            var item = lvNvPresets.SelectedItems[0];
            lvNvPresets.Items.Remove(item);
        }

        private void btnAddModesNv_Click(object sender, EventArgs e)
        {
            var presets = NvPreset.GetDefaultPresets();
            var added = false;

            foreach (var preset in presets)
            {
                if (!_nvService.GetPresets().Any(x => x.colorData.Equals(preset.colorData)))
                {
                    AddOrUpdateItem(preset);
                    added = true;
                }
            }

            if (added)
            {
                MessageForms.InfoOk("Missing presets added.");
            }
            else
            {
                MessageForms.InfoOk("All presets for every color setting already exist.");
            }
        }

        private void tabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            InitSelectedTab();
        }

        private void InitSelectedTab()
        {
            if (tcMain.SelectedTab == tabLG)
            {
                InitLgTab();
            }
            else if (tcMain.SelectedTab == tabLog)
            {
                LoadLog();
            }
            else if (tcMain.SelectedTab == tabInfo)
            {
                LoadInfo();
            }
            else if (tcMain.SelectedTab == tabOptions)
            {
                InitOptionsTab();
            }
        }

        private void InitLgTab()
        {
            if (scLgController.Panel2.Controls.Count == 0)
            {
                var rcPanel = new RemoteControlPanel(_lgService, _lgService.GetRemoteControlButtons());
                rcPanel.Parent = scLgController.Panel2;
                rcPanel.Dock = DockStyle.Fill;
            }
            chkLgRemoteControlShow.Checked = _lgService.Config.ShowRemoteControl;
            scLgController.Panel2Collapsed = !_lgService.Config.ShowRemoteControl;

            Utils.BuildComboBox<PresetTriggerType>(cbxLgPresetTrigger, PresetTriggerType.Resume, PresetTriggerType.Shutdown, PresetTriggerType.Standby, PresetTriggerType.Startup, PresetTriggerType.Reserved5, PresetTriggerType.ScreensaverStart, PresetTriggerType.ScreensaverStop);
        }

        private void InitOptionsTab()
        {
            grpNvidiaOptions.Visible = _nvService != null;
            if (grpNvidiaOptions.Visible)
            {
                var firstTime = cbxDitheringBitDepth.Items.Count == 0;

                if (firstTime)
                {
                    cbxDitheringBitDepth.Items.AddRange(Utils.GetDescriptions<NvDitherBits>().ToArray());
                    cbxDitheringMode.Items.AddRange(Utils.GetDescriptions<NvDitherMode>().ToArray());
                }

                var preset = _nvService.GetLastAppliedPreset() ?? GetSelectedNvPreset();
                if (firstTime || preset != null)
                {
                    var ditheringEnabled = true;
                    int ditherBitsIndex;
                    int ditherModeIndex;
                    if (preset == null)
                    {
                        var hdrEnabled = _nvService.IsHDREnabled();
                        ditherBitsIndex = (int)(hdrEnabled ? NvDitherBits.Bits8 : NvDitherBits.Bits6);
                        ditherModeIndex = (int)NvDitherMode.Temporal;
                    }
                    else
                    {
                        ditheringEnabled = preset.ditheringEnabled;
                        ditherBitsIndex = (int)preset.ditheringBits;
                        ditherModeIndex = (int)preset.ditheringMode;
                    }

                    chkDitheringEnabled.Checked = ditheringEnabled;
                    cbxDitheringBitDepth.SelectedIndex = ditherBitsIndex;
                    cbxDitheringMode.SelectedIndex = ditherModeIndex;
                    FillGradient();
                }
            }
        }

        private void FillLgDevices()
        {
            var devices = _lgService.Devices;

            cbxLgDevices.Items.Clear();
            cbxLgDevices.Items.AddRange(devices.ToArray());

            cbxLgPresetDevice.Items.Clear();
            cbxLgPresetDevice.Items.AddRange(devices.ToArray());
            var globalDevice = new LgDevice("Globally selected device", string.Empty, string.Empty, true, true);
            cbxLgPresetDevice.Items.Insert(0, globalDevice);

            var device = _lgService.SelectedDevice;

            if (device != null)
            {
                cbxLgDevices.SelectedIndex = cbxLgDevices.Items.IndexOf(device);
            }

            if (!devices.Any())
            {
                MessageForms.WarningOk("It seems there's no LG TV available! Please make sure it's connected to the same network as this PC.");
            }

            if (cbxLgApps.Items.Count == 0 && device != null)
            {
                _lgService.RefreshApps().ContinueWith((task) => BeginInvoke(new Action(FillLgApps)));
            }

            btnLgDeviceConvertToCustom.Enabled = devices.Any();
            btnLgExpert.Enabled = devices.Any();
            btnLgGameBar.Enabled = devices.Any();

            SetLgDevicePowerOptions();
        }

        private void SetLgDevicePowerOptions()
        {
            var device = _lgService.SelectedDevice;
            clbLgPower.Enabled = device != null;

            _disableEvents = true;
            try
            {
                clbLgPower.SetItemChecked(0, device?.PowerOnAfterStartup ?? false);
                clbLgPower.SetItemChecked(1, device?.PowerOnAfterResume ?? false);
                clbLgPower.SetItemChecked(2, device?.PowerOffOnShutdown ?? false);
                clbLgPower.SetItemChecked(3, device?.PowerOffOnStandby ?? false);
                clbLgPower.SetItemChecked(4, device?.PowerSwitchOnScreenSaver ?? false);
            }
            finally
            {
                _disableEvents = false;
            }
        }

        private void FillLgApps()
        {
            var lgApps = _lgService?.GetApps();
            if (lgApps == null || !lgApps.Any())
            {
                MessageForms.WarningOk("Could not refresh the apps. Check the log for details.");
                return;
            }
            InitLgApps();
        }

        private void InitLgApps() {
            var lgApps = _lgService?.GetApps();

            cbxLgApps.Items.Clear();
            if (lgApps != null)
            {
                cbxLgApps.Items.AddRange(lgApps.ToArray());
            }

            for (var i = 0; i < lvLgPresets.Items.Count; i++)
            {
                var item = lvLgPresets.Items[i];
                AddOrUpdateItemLg((LgPreset)item.Tag, item);
            }

            var preset = GetSelectedLgPreset();
            if (preset != null)
            {
                cbxLgApps.SelectedIndex = lgApps == null ? -1 : lgApps.FindIndex(x => x.appId.Equals(preset.appId));
            }
        }

        private void UpdateTrayMenuNv()
        {
            var presets = _nvService.GetPresets().Where(x => x.applyColorData || x.applyDithering || x.applyHDR || x.applyRefreshRate);

            _nvTrayMenu.DropDownItems.Clear();

            foreach (var preset in presets)
            {
                var name = preset.GetTextForMenuItem();
                if (!string.IsNullOrEmpty(preset.shortcut))
                {
                    name += "\t" + preset.shortcut;
                }

                var item = new ToolStripMenuItem(name);
                item.Tag = preset;
                item.Click += TrayMenuItemNv_Click;
                _nvTrayMenu.DropDownItems.Add(item);
            }
        }

        private void UpdateTrayMenuAmd()
        {
            var presets = _amdService.GetPresets().Where(x => x.applyColorData || x.applyDithering || x.applyHDR || x.applyRefreshRate);

            _amdTrayMenu.DropDownItems.Clear();

            foreach (var preset in presets)
            {
                var name = preset.GetTextForMenuItem();
                if (!string.IsNullOrEmpty(preset.shortcut))
                {
                    name += "\t" + preset.shortcut;
                }

                var item = new ToolStripMenuItem(name);
                item.Tag = preset;
                item.Click += TrayMenuItemAmd_Click;
                _amdTrayMenu.DropDownItems.Add(item);
            }
        }

        private void TrayMenuItemNv_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            var preset = (NvPreset)item.Tag;

            ApplyNvPreset(preset);
        }

        private void TrayMenuItemAmd_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            var preset = (AmdPreset)item.Tag;

            ApplyAmdPreset(preset);
        }

        private void trayIconContextMenu_Popup(object sender, EventArgs e)
        {
            _nvTrayMenu.Visible = _nvService != null;
            if (_nvTrayMenu.Visible)
            {
                UpdateTrayMenuNv();
            }

            _amdTrayMenu.Visible = _amdService != null;
            if (_amdTrayMenu.Visible)
            {
                UpdateTrayMenuAmd();
            }

            _lgTrayMenu.Visible = _lgService != null;
            if (_lgTrayMenu.Visible)
            {
                UpdateTrayMenuLg();
            }
        }

        private void UpdateTrayMenuLg()
        {
            var presets = _lgService.GetPresets().Where(x => !string.IsNullOrEmpty(x.appId) || x.steps.Any());

            _lgTrayMenu.DropDownItems.Clear();

            foreach (var preset in presets)
            {
                var name = preset.name;
                if (!string.IsNullOrEmpty(preset.shortcut))
                {
                    name += "\t" + preset.shortcut;
                }

                var item = new ToolStripMenuItem(name);
                item.Tag = preset;
                item.Click += TrayMenuItemLg_Click;
                _lgTrayMenu.DropDownItems.Add(item);
            }
        }

        private void TrayMenuItemLg_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            var preset = (LgPreset)item.Tag;

            ApplyLgPreset(preset);
        }

        private void ApplySelectedNvPreset()
        {
            var preset = GetSelectedNvPreset();
            ApplyNvPreset(preset);
        }

        private void ApplySelectedAmdPreset()
        {
            var preset = GetSelectedAmdPreset();
            ApplyAmdPreset(preset);
        }

        private void btnDeleteLg_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedLgPreset();

            if (preset == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(preset.shortcut))
            {
                Utils.UnregisterHotKey(Handle, preset.id);
            }

            _lgService.GetPresets().Remove(preset);

            var item = lvLgPresets.SelectedItems[0];
            lvLgPresets.Items.Remove(item);
        }

        private void btnAddLg_Click(object sender, EventArgs e)
        {
            AddOrUpdateItemLg(_lgService.CreateNewPreset());
        }

        private void edtDelayDisplaySettings_ValueChanged(object sender, EventArgs e)
        {
            _config.DisplaySettingsDelay = (int)edtDelayDisplaySettings.Value;
        }

        private bool ApplyNvPreset(NvPreset preset)
        {
            if (preset == null || _nvService == null)
            {
                return false;
            }
            try
            {
                var result = _nvService.ApplyPreset(preset, Program.AppContext);
                if (!result)
                {
                    throw new Exception("Error while applying NVIDIA preset. At least one setting could not be applied. Check the log for details.");
                }

                return true;
            }
            catch (Exception e)
            {
                MessageForms.ErrorOk($"Error applying NVIDIA-preset ({e.TargetSite.Name}): {e.Message}");
                return false;
            }
        }

        private bool ApplyAmdPreset(AmdPreset preset)
        {
            if (preset == null || _amdService == null)
            {
                return false;
            }
            try
            {
                var result = _amdService.ApplyPreset(preset, Program.AppContext);
                if (!result)
                {
                    throw new Exception("Error while applying AMD preset. At least one setting could not be applied. Check the log for details.");
                }

                return true;
            }
            catch (Exception e)
            {
                MessageForms.ErrorOk($"Error applying AMD-preset ({e.TargetSite.Name}): {e.Message}");
                return false;
            }
        }

        private void ApplyLgPreset(LgPreset preset, bool reconnect = false, bool wait = false)
        {
            if (preset == null)
            {
                return;
            }

            var applyTask = _lgService.ApplyPreset(preset, reconnect).ContinueWith((task) => BeginInvoke(new Action<Task<bool>>(LgPresetApplied), new[] { task }));
            if (wait)
            {
                Utils.WaitForTask(applyTask);
            }
        }

        private void LgPresetApplied(Task<bool> task)
        {
            var result = task.Result;
            if (!result)
            {
                MessageForms.WarningOk("Could not apply the preset (entirely). Check the log for details.");
            }
        }

        private void LoadLog()
        {
            var filename = Path.Combine(_dataDir, "LogFile.txt");

            var lines = new[] { "No log file found" };
            if (File.Exists(filename))
            {
                lines = File.ReadAllLines(filename);
            }
            var reversedLines = lines.ToList();
            var builder = new StringBuilder();
            reversedLines.ForEach(line => builder.AppendLine(line));
            edtLog.Text = builder.ToString();
        }

        private void LoadInfo()
        {
            grpNVIDIAInfo.Visible = _nvService != null;
        }

        private void btnLgRefreshApps_Click(object sender, EventArgs e)
        {
            _lgService.RefreshApps(true).ContinueWith((task) => BeginInvoke(new Action(FillLgApps)));
        }

        private void cbxLgDevices_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cbxLgDevices.SelectedIndex == -1)
            {
                _lgService.SelectedDevice = null;
            }
            else
            {
                _lgService.SelectedDevice = (LgDevice)cbxLgDevices.SelectedItem;
            }
            chkLgRemoteControlShow.Enabled = _lgService.SelectedDevice != null;
            btnLgRemoveDevice.Enabled = _lgService.SelectedDevice != null && _lgService.SelectedDevice.IsCustom;
            btnLgDeviceConvertToCustom.Enabled = _lgService.SelectedDevice != null && !_lgService.SelectedDevice.IsCustom;

            SetLgDevicePowerOptions();
        }

        private void lvLgPresets_DoubleClick(object sender, EventArgs e)
        {
            ApplySelectedLgPreset();
        }

        private void ApplySelectedLgPreset()
        {
            var preset = GetSelectedLgPreset();
            ApplyLgPreset(preset);
        }

        private void btnRefreshNVIDIAInfo_Click(object sender, EventArgs e)
        {
            if (_nvService != null)
            {
                tvNVIDIAInfo.Nodes.Clear();
                var displays = Display.GetDisplays();
                for (var i = 0; i < displays.Length; i++)
                {
                    var display = displays[i];
                    var node = TreeNodeBuilder.CreateTree(display, $"Display[{i}]");
                    tvNVIDIAInfo.Nodes.Add(node);
                }
            }
        }

        private void btnLgAddButton_Click(object sender, EventArgs e)
        {
            mnuLgButtons.Show(btnLgAddButton, btnLgAddButton.PointToClient(Cursor.Position));
        }

        private void miLgAddButton_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripItem;

            AddToLgSteps(item.Text);
        }

        private void miLgAddAction_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripItem;
            var action = item.Tag as LgDevice.InvokableAction;

            var text = item.Text;

            var value = string.Empty;

            if (action.EnumType == null)
            {
                if (action.MinValue != action.MaxValue)
                {
                    var values = MessageForms.ShowDialog("Enter value", new[] {
                    new MessageForms.FieldDefinition
                        {
                            Label = "Enter desired " + text,
                            FieldType = MessageForms.FieldType.Numeric,
                            MinValue = action.MinValue,
                            MaxValue = action.MaxValue
                        }
                    });

                    if (!values.Any())
                    {
                        return;
                    }

                    value = values.First().Value.ToString();
                }
            }
            else
            {
                var dropDownValues = new List<string>();
                foreach (var enumValue in Enum.GetValues(action.EnumType))
                {
                    dropDownValues.Add(enumValue.ToString().Replace("_", ""));
                }

                var values = MessageForms.ShowDialog("Choose value", new[] {
                    new MessageForms.FieldDefinition
                    { 
                        Label = "Choose desired " + text, 
                        FieldType = MessageForms.FieldType.DropDown, 
                        Values = dropDownValues 
                    } 
                });

                if (!values.Any())
                {
                    return;
                }

                value = values.First().Value.ToString();
            }

            if (!string.IsNullOrEmpty(value))
            {
                text += $"({value})";
            }

            AddToLgSteps(text);
        }

        private void miLgAddNvPreset_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripItem;
            var preset = item.Tag as NvPreset;

            var text = $"NvPreset({preset.name})";

            AddToLgSteps(text);
        }

        private void miLgAddAmdPreset_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripItem;
            var preset = item.Tag as AmdPreset;

            var text = $"AmdPreset({preset.name})";

            AddToLgSteps(text);
        }

        private void AddToLgSteps(string step)
        {
            var text = edtStepsLg.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = step;
            }
            else
            {
                var pos = edtStepsLg.SelectionStart;
                while (pos < text.Length && text.CharAt(pos) != ',')
                {
                    pos++;
                }
                if (pos == text.Length)
                {
                    text += ", " + step;
                }
                else
                {
                    text = text.Substring(0, pos + 1) + " " + step + ", " + text.Substring(pos + 1).Trim();
                }
            }
            edtStepsLg.Text = text.Trim();
        }

        private void clbLgPower_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (!_initialized || _disableEvents)
            {
                return;
            }

            var device = _lgService?.SelectedDevice;
            if (device == null)
            {
                return;
            }

            if (!(device.PowerOnAfterResume || device.PowerOnAfterStartup || device.PowerSwitchOnScreenSaver))
            {
                MessageForms.InfoOk(
@"Be sure to activate the following setting on the TV, or the app will not be able to wake the TV:

Connection > Mobile TV On > Turn on via Wi-Fi (Networked Standby Mode)

See Options to test this functionality."
                );
            }

            BeginInvoke(new Action(() =>
            {
                device.PowerOnAfterStartup = clbLgPower.GetItemChecked(0);
                device.PowerOnAfterResume = clbLgPower.GetItemChecked(1);
                device.PowerOffOnShutdown = clbLgPower.GetItemChecked(2);
                device.PowerOffOnStandby = clbLgPower.GetItemChecked(3);
                device.PowerSwitchOnScreenSaver = clbLgPower.GetItemChecked(4);

                _lgService.InstallEventHandlers();
            }));
        }

        private void edtLgPowerOnAfterResumeDelay_ValueChanged(object sender, EventArgs e)
        {
            _lgService.Config.PowerOnRetries = (int)edtLgMaxPowerOnRetries.Value;
        }

        private void chkFixChromeFonts_CheckedChanged(object sender, EventArgs e)
        {
            if (_initialized)
            {
                _config.FixChromeFonts = chkFixChromeFonts.Checked;
                if (chkFixChromeFonts.Checked)
                {
                    Utils.ExecuteElevated(StartUpParams.ActivateChromeFontFixParam);
                }
                else
                {
                    Utils.ExecuteElevated(StartUpParams.DeactivateChromeFontFixParam);
                }
            }
        }

        private void btnLGTestPower_Click(object sender, EventArgs e)
        {
            //_lgService.WakeSelectedDevice();
            //return;
            var text =
@"The TV will now power off. Please wait for the TV to be powered off completely (relay click) and press ENTER to wake it again.
For waking up to work, you need to activate the following setting on the TV:

Connection > Mobile TV On > Turn on via Wi-Fi

It will also work over a wired connection.
Do you want to continue?";

            if (MessageForms.QuestionYesNo(text) == DialogResult.Yes)
            {
                Utils.WaitForTask(_lgService.PowerOff());

                MessageForms.InfoOk("Press ENTER to wake the TV.");

                _lgService.WakeSelectedDevice();
            }
        }

        private void btnSetShortcutScreenSaver_Click(object sender, EventArgs e)
        {
            var shortcut = edtBlankScreenSaverShortcut.Text.Trim();

            if (!Utils.ValidateShortcut(shortcut))
            {
                return;
            }

            var oldShortcut = _config.ScreenSaverShortcut;

            var clear = !string.IsNullOrEmpty(oldShortcut);

            _config.ScreenSaverShortcut = shortcut;

            Utils.RegisterShortcut(Handle, SHORTCUTID_SCREENSAVER, shortcut, clear);
        }

        private void chkMinimizeOnClose_CheckedChanged(object sender, EventArgs e)
        {
            _config.MinimizeOnClose = chkMinimizeOnClose.Checked;
        }

        private void MainForm_Activated(object sender, EventArgs e)
        {
            UpdateDisplayInfoItems();
            UpdateDisplayInfoItemsAmd();

            if (tcMain.SelectedTab == tabLog)
            {
                LoadLog();
            }
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            var filename = Path.Combine(_dataDir, "LogFile.txt");
            if (File.Exists(filename))
            {
                File.Delete(filename);
                edtLog.Clear();
            }
        }

        private void edtLgDeviceFilter_TextChanged(object sender, EventArgs e)
        {
            if (_lgService != null)
            {
                _lgService.Config.DeviceSearchKey = edtLgDeviceFilter.Text;
            }
        }

        private void btnLgDeviceFilterRefresh_Click(object sender, EventArgs e)
        {
            RefreshLgDevices();
        }

        private void RefreshLgDevices()
        {
            _lgService.RefreshDevices(false).ContinueWith((task) => BeginInvoke(new Action(FillLgDevices)));
        }

        private void FillGradient()
        {
            if (pbGradient.Image == null)
            {
                pbGradient.Image = Utils.GenerateGradientBitmap(pbGradient.Width, pbGradient.Height);
            }
        }

        private void chkDitheringEnabled_CheckedChanged(object sender, EventArgs e)
        {
            ApplyDitheringOptions();
        }

        private void ApplyDitheringOptions()
        {
            var bitDepth = cbxDitheringBitDepth.SelectedIndex;
            var mode = cbxDitheringMode.SelectedIndex;

            _nvService.SetDithering(chkDitheringEnabled.Checked, (uint)bitDepth, (uint)(mode > -1 ? mode : (int)NvDitherMode.Temporal));
        }

        private void cbxDitheringBitDepth_SelectedIndexChanged(object sender, EventArgs e)
        {
            ApplyDitheringOptions();
        }

        private void cbxDitheringMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            ApplyDitheringOptions();
        }

        private void miNvDithering6bit_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;

            var preset = GetSelectedNvPreset();

            preset.ditheringBits = uint.Parse(item.Tag.ToString());

            AddOrUpdateItem();
        }

        private void spatial1ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;

            var preset = GetSelectedNvPreset();

            preset.ditheringMode = uint.Parse(item.Tag.ToString());

            AddOrUpdateItem();

        }

        private void btnLGRemoteControl_Click(object sender, EventArgs e)
        {
        }

        private void AfterInitialized()
        {
            ApplyNvPresetOnStartup();
            ApplyAmdPresetOnStartup();
            if (_trayIcon.Visible)
            {
                CheckForUpdates();
            }
        }

        private void CheckForUpdates()
        {
            if (!_config.CheckForUpdates || _checkedForUpdates || Debugger.IsAttached)
            {
                return;
            }

            _checkedForUpdates = true;

            var _ = Utils.GetRestJsonAsync("https://api.github.com/repos/maassoft/colorcontrol/releases/latest", InitHandleCheckForUpdates);
        }

        private void InitHandleCheckForUpdates(dynamic latest)
        {
            BeginInvoke(new Action<dynamic>(HandleCheckForUpdates), new[] { latest });
        }

        private void HandleCheckForUpdates(dynamic latest)
        {
            if (latest?.tag_name == null)
            {
                return;
            }

            var currentVersion = Application.ProductVersion;

            var newVersion = latest.tag_name.Value.Substring(1);
            if (newVersion.CompareTo(currentVersion) > 0)
            {
                _updateHtmlUrl = latest.html_url.Value;

                if (_trayIcon.Visible)
                {
                    _trayIcon.ShowBalloonTip(5000, "Update available", $"Version {newVersion} is available. Click to open the GitHub page", ToolTipIcon.Info);
                }
                else
                {
                    MessageForms.InfoOk($"New version {newVersion} is available. Click on the Help-button to open the GitHub page.", "Update available", "https://github.com/Maassoft/ColorControl/releases/tag/v4.0.0.0");
                }
            }
        }

        private void ApplyNvPresetOnStartup(int attempts = 5) {

            var presetIdOrName = !string.IsNullOrEmpty(StartUpParams.NvidiaPresetIdOrName) ? StartUpParams.NvidiaPresetIdOrName : _config.NvPresetId_ApplyOnStartup.ToString();

            if (!string.IsNullOrEmpty(presetIdOrName))
            {
                var preset = _nvService?.GetPresetByIdOrName(presetIdOrName);
                if (preset == null)
                {
                    if (string.IsNullOrEmpty(StartUpParams.NvidiaPresetIdOrName))
                    {
                        _config.NvPresetId_ApplyOnStartup = 0;
                    }
                }
                else if (_nvService != null)
                {
                    if (_nvService.HasDisplaysAttached())
                    {
                        ApplyNvPreset(preset);
                    }
                    else
                    {
                        attempts--;
                        if (attempts > 0)
                        {
                            Task.Run(async () =>
                            {
                                await Task.Delay(2000);
                                BeginInvoke(new Action(() => ApplyNvPresetOnStartup(attempts)));
                            });
                        }
                    }
                }
            }
        }

        private void ApplyAmdPresetOnStartup(int attempts = 5)
        {
            var presetIdOrName = !string.IsNullOrEmpty(StartUpParams.AmdPresetIdOrName) ? StartUpParams.AmdPresetIdOrName : _config.AmdPresetId_ApplyOnStartup.ToString();

            if (!string.IsNullOrEmpty(presetIdOrName))
            {
                var preset = _amdService?.GetPresetByIdOrName(presetIdOrName);
                if (preset == null)
                {
                    if (string.IsNullOrEmpty(StartUpParams.AmdPresetIdOrName))
                    {
                        _config.AmdPresetId_ApplyOnStartup = 0;
                    }
                }
                else if (_amdService != null)
                {
                    if (_amdService.HasDisplaysAttached())
                    {
                        ApplyAmdPreset(preset);
                    }
                    else
                    {
                        attempts--;
                        if (attempts > 0)
                        {
                            Task.Run(async () =>
                            {
                                await Task.Delay(2000);
                                BeginInvoke(new Action(() => ApplyAmdPresetOnStartup(attempts)));
                            });
                        }
                    }
                }
            }
        }

        private void miNvPresetApplyOnStartup_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();
            _config.NvPresetId_ApplyOnStartup = miNvPresetApplyOnStartup.Checked ? preset.id : 0;

            AddOrUpdateItem();
        }

        private void MainForm_Deactivate(object sender, EventArgs e)
        {
            GlobalSave();
        }

        private void btnApplyAmd_Click(object sender, EventArgs e)
        {
            ApplySelectedAmdPreset();
        }

        private void btnChangeAmd_Click(object sender, EventArgs e)
        {
            mnuAmdPresets.Show(btnChangeAmd, btnChangeAmd.PointToClient(Cursor.Position));
        }

        private void edtAmdShortcut_TextChanged(object sender, EventArgs e)
        {
            var text = edtAmdShortcut.Text;

            var preset = GetSelectedAmdPreset();

            if (preset == null || string.IsNullOrEmpty(text))
            {
                edtAmdShortcut.ForeColor = SystemColors.WindowText;
            }
            else
            {
                edtAmdShortcut.ForeColor = ShortCutExists(text, preset.id) ? Color.Red : SystemColors.WindowText;
            }
        }

        private bool ShortCutExists(string shortcut, int presetId = 0)
        {
            return
                (_nvService?.GetPresets().Any(x => x.id != presetId && shortcut.Equals(x.shortcut)) ?? false) ||
                (_amdService?.GetPresets().Any(x => x.id != presetId && shortcut.Equals(x.shortcut)) ?? false) ||
                (_lgService?.GetPresets().Any(x => x.id != presetId && shortcut.Equals(x.shortcut)) ?? false);
        }

        private void edtShortcutLg_TextChanged(object sender, EventArgs e)
        {
            var text = edtShortcutLg.Text;

            var preset = GetSelectedLgPreset();

            if (preset == null || string.IsNullOrEmpty(text))
            {
                edtShortcutLg.ForeColor = SystemColors.WindowText;
            }
            else
            {
                edtShortcutLg.ForeColor = ShortCutExists(text, preset.id) ? Color.Red : SystemColors.WindowText;
            }
        }

        private void btnSetAmdShortcut_Click(object sender, EventArgs e)
        {
            var shortcut = edtAmdShortcut.Text.Trim();

            if (!Utils.ValidateShortcut(shortcut))
            {
                return;
            }

            var preset = GetSelectedAmdPreset();

            var name = edtAmdPresetName.Text.Trim();

            if (!string.IsNullOrEmpty(name) && _amdService.GetPresets().Any(x => x.id != preset.id && x.name != null && x.name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageForms.WarningOk("The name must be unique.");
                return;
            }

            var clear = !string.IsNullOrEmpty(preset.shortcut);

            preset.shortcut = shortcut;
            preset.name = name;

            AddOrUpdateItemAmd();

            Utils.RegisterShortcut(Handle, preset.id, preset.shortcut, clear);
        }

        private void btnCloneAmd_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            var newPreset = preset.Clone();
            AddOrUpdateItemAmd(newPreset);
        }

        private void btnDeleteAmd_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            if (preset == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(preset.shortcut))
            {
                Utils.UnregisterHotKey(Handle, preset.id);
            }

            _amdService.GetPresets().Remove(preset);

            var item = lvAmdPresets.SelectedItems[0];
            lvAmdPresets.Items.Remove(item);
        }

        private void mnuAmdPresets_Opening(object sender, CancelEventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            miAmdApply.Enabled = preset != null;
            miAmdPresetApplyOnStartup.Enabled = preset != null;
            mnuAmdDisplay.Enabled = preset != null;
            mnuAmdColorSettings.Enabled = preset != null;
            mnuAmdRefreshRate.Enabled = preset != null;
            mnuAmdDithering.Enabled = preset != null;
            mnuAmdHDR.Enabled = preset != null;

            if (preset != null)
            {
                miAmdPresetApplyOnStartup.Checked = _config.AmdPresetId_ApplyOnStartup == preset.id;

                if (mnuAmdDisplay.DropDownItems.Count == 1)
                {
                    var displays = _amdService.GetDisplays();
                    for (var i = 0; i < displays.Count; i++)
                    {
                        var display = displays[i];
                        var name = _amdService.GetFullDisplayName(display);

                        var item = mnuAmdDisplay.DropDownItems.Add(name);
                        item.Tag = display;
                        item.Click += displayMenuItemAmd_Click;
                    }
                }

                Utils.BuildDropDownMenu(mnuAmdColorSettings, "Color depth", typeof(ADLColorDepth), preset, "colorDepth", amdPresetColorDataMenuItem_Click);
                Utils.BuildDropDownMenu(mnuAmdColorSettings, "Pixel format", typeof(ADLPixelFormat), preset, "pixelFormat", amdPresetColorDataMenuItem_Click);

                if (preset.displayName != _lastDisplayRefreshRates)
                {
                    while (mnuAmdRefreshRate.DropDownItems.Count > 1)
                    {
                        mnuAmdRefreshRate.DropDownItems.RemoveAt(mnuAmdRefreshRate.DropDownItems.Count - 1);
                    }
                }

                if (mnuAmdRefreshRate.DropDownItems.Count == 1)
                {
                    var refreshRates = _amdService.GetAvailableRefreshRates(preset);
                    _lastDisplayRefreshRates = preset.displayName;

                    foreach (var refreshRate in refreshRates)
                    {
                        var item = mnuAmdRefreshRate.DropDownItems.Add(refreshRate.ToString() + "Hz");
                        item.Tag = refreshRate;
                        item.Click += refreshRateMenuItemAmd_Click;
                    }
                }

                miAmdPrimaryDisplay.Checked = preset.primaryDisplay;
                foreach (var item in mnuAmdDisplay.DropDownItems)
                {
                    if (item is ToolStripMenuItem)
                    {
                        var menuItem = (ToolStripMenuItem)item;
                        if (menuItem.Tag != null)
                        {
                            menuItem.Checked = ((ADLDisplayInfo)menuItem.Tag).DisplayName.Equals(preset.displayName);
                        }
                    }
                }

                miAmdColorSettingsIncluded.Checked = preset.applyColorData;

                miAmdRefreshRateIncluded.Checked = preset.applyRefreshRate;
                foreach (var item in mnuAmdRefreshRate.DropDownItems)
                {
                    if (item is ToolStripMenuItem)
                    {
                        var menuItem = (ToolStripMenuItem)item;
                        if (menuItem.Tag != null)
                        {
                            menuItem.Checked = (uint)menuItem.Tag == preset.refreshRate;
                        }
                    }
                }

                miAmdDitheringIncluded.Checked = preset.applyDithering;

                Utils.BuildDropDownMenu(mnuAmdDithering, "Mode", typeof(ADLDitherState), preset, "ditherState", amdPresetColorDataMenuItem_Click);

                miAmdHDRIncluded.Checked = preset.applyHDR;
                miAmdHDREnabled.Checked = preset.HDREnabled;
                miAmdHDRToggle.Checked = preset.toggleHDR;
            }
        }

        private void miAmdPresetApplyOnStartup_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();
            _config.AmdPresetId_ApplyOnStartup = miAmdPresetApplyOnStartup.Checked ? preset.id : 0;

            AddOrUpdateItemAmd();
        }

        private void lvAmdPresets_SelectedIndexChanged(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();
            var enabled = preset != null;

            btnApplyAmd.Enabled = enabled;
            btnChangeAmd.Enabled = enabled;
            edtAmdPresetName.Enabled = enabled;
            edtAmdShortcut.Enabled = enabled;
            btnAmdPresetSave.Enabled = enabled;
            btnCloneAmd.Enabled = enabled;
            btnDeleteAmd.Enabled = enabled;

            if (preset != null)
            {
                edtAmdPresetName.Text = preset.name;
                edtAmdShortcut.Text = preset.shortcut;
            }
            else
            {
                edtAmdPresetName.Text = string.Empty;
                edtAmdShortcut.Text = string.Empty;
            }
        }

        private void amdPresetColorDataMenuItem_Click(object sender, EventArgs e)
        {
            var menuItem = (ToolStripMenuItem)sender;
            var property = (PropertyInfo)menuItem.OwnerItem.Tag;

            var value = menuItem.Tag;

            var preset = GetSelectedAmdPreset();

            property.SetValue(preset, value);

            AddOrUpdateItemAmd();
        }

        private void miAmdColorSettingsIncluded_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            preset.applyColorData = !preset.applyColorData;

            AddOrUpdateItemAmd();
        }

        private void miAmdRefreshRateIncluded_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            preset.applyRefreshRate = !preset.applyRefreshRate;

            AddOrUpdateItemAmd();
        }

        private void miAmdDitheringIncluded_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            preset.applyDithering = !preset.applyDithering;

            AddOrUpdateItemAmd();
        }

        private void miAmdHDRIncluded_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            preset.applyHDR = !preset.applyHDR;

            AddOrUpdateItemAmd();
        }

        private void miAmdHDRToggle_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            preset.toggleHDR = !preset.toggleHDR;

            if (preset.toggleHDR)
            {
                preset.HDREnabled = false;
            }

            AddOrUpdateItemAmd();
        }

        private void miAmdHDREnabled_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            preset.HDREnabled = !preset.HDREnabled;
            preset.toggleHDR = false;

            AddOrUpdateItemAmd();
        }

        private void btnAddAmd_Click(object sender, EventArgs e)
        {
            var presets = AmdPreset.GetDefaultPresets();
            var added = false;

            foreach (var preset in presets)
            {
                AddOrUpdateItemAmd(preset);
            }

            if (added)
            {
                MessageForms.InfoOk("Missing presets added.");
            }
            else
            {
                MessageForms.InfoOk("All presets for every color setting already exist.");
            }
        }

        private void chkMinimizeToSystemTray_CheckedChanged(object sender, EventArgs e)
        {
            if (_initialized)
            {
                _config.MinimizeToTray = chkMinimizeToSystemTray.Checked;
                _trayIcon.Visible = _config.MinimizeToTray;
            }
        }

        private void miNvCopyId_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedNvPreset();

            if (preset != null)
            {
                Clipboard.SetText(preset.id.ToString());
            }
        }

        private void miAmdCopyId_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedAmdPreset();

            if (preset != null)
            {
                Clipboard.SetText(preset.id.ToString());
            }
        }

        private void btnLgAddDevice_Click(object sender, EventArgs e)
        {
            var values = MessageForms.ShowDialog("Add tv", new[] { "Name", "Ip-address", "MAC-address" }, ValidateAddDevice);
            if (values.Any())
            {
                var device = new LgDevice(values[0].Value.ToString(), values[1].Value.ToString(), values[2].Value.ToString());

                var form = MessageForms.ShowProgress("Connecting to device...");

                var result = false;
                Enabled = false;
                try
                {
                    var task = device.TestConnection();

                    result = Utils.WaitForTask(task);
                }
                finally
                {
                    form.Close();
                    Enabled = true;
                }

                if (!result && MessageForms.QuestionYesNo("Unable to connect to the device. Are you sure you want to add it?") != DialogResult.Yes)
                {
                    return;
                }

                _lgService.AddCustomDevice(device);

                FillLgDevices();
            }
        }

        private string ValidateAddDevice(IEnumerable<MessageForms.FieldDefinition> values)
        {
            if (values.Any(v => string.IsNullOrEmpty(v.Value?.ToString())))
            {
                return "Please fill in all the fields";
            }

            return null;
        }

        private void btnLgRemoveDevice_Click(object sender, EventArgs e)
        {
            if (MessageForms.QuestionYesNo("Are you sure you want to remove this device?") != DialogResult.Yes)
            {
                return;
            }
            
            var device = _lgService.SelectedDevice;

            if (device != null)
            {
                _lgService.RemoveCustomDevice(device);

                FillLgDevices();
            }
        }

        private void mnuLgButtons_Opening(object sender, CancelEventArgs e)
        {
            mnuLgActions.DropDownItems.Clear();

            var preset = GetSelectedLgPreset();

            if (preset == null)
            {
                return;
            }

            var device = _lgService.GetPresetDevice(preset);

            var actions = device?.GetInvokableActions();
            foreach (var action in actions)
            {
                var text = action.Name;

                var item = mnuLgActions.DropDownItems.Add(text);
                item.Tag = action;
                item.Click += miLgAddAction_Click;
            }

            if (_nvService != null)
            {
                mnuLgNvPresets.DropDownItems.Clear();

                foreach (var nvPreset in _nvService.GetPresets())
                {
                    var text = nvPreset.name;

                    if (!string.IsNullOrEmpty(text))
                    {
                        var item = mnuLgNvPresets.DropDownItems.Add(text);
                        item.Tag = nvPreset;
                        item.Click += miLgAddNvPreset_Click;
                    }
                }
            }

            if (_amdService != null)
            {
                mnuLgAmdPresets.DropDownItems.Clear();

                foreach (var amdPreset in _amdService.GetPresets())
                {
                    var text = amdPreset.name;

                    if (!string.IsNullOrEmpty(text))
                    {
                        var item = mnuLgAmdPresets.DropDownItems.Add(text);
                        item.Tag = amdPreset;
                        item.Click += miLgAddAmdPreset_Click;
                    }
                }
            }

            mnuLgNvPresets.Visible = _nvService != null;
            mnuLgAmdPresets.Visible = _amdService != null;

            mnuLgNvPresets.Text = mnuLgNvPresets.DropDownItems.Count > 0 ? "NVIDIA presets" : "NVIDIA presets (no named presets found)";
            mnuLgAmdPresets.Text = mnuLgAmdPresets.DropDownItems.Count > 0 ? "AMD presets" : "AMD presets (no named presets found)";
        }

        private void btnLgDeviceConvertToCustom_Click(object sender, EventArgs e)
        {
            if (MessageForms.QuestionYesNo(
@"This will convert the automatically detected device to a custom variant.
This means that the device will remain here even if it is not detected anymore.
Do you want to continue?"
               ) != DialogResult.Yes)
            {
                return;
            }

            _lgService.SelectedDevice.ConvertToCustom();

            FillLgDevices();
        }

        private void btnLgRemoteControlShow_Click(object sender, EventArgs e)
        {
        }

        private void chkLgRemoteControlShow_CheckedChanged(object sender, EventArgs e)
        {
            scLgController.Panel2Collapsed = !chkLgRemoteControlShow.Checked;
            _lgService.Config.ShowRemoteControl = chkLgRemoteControlShow.Checked;
        }

        private void mnuLgExpert_Opening(object sender, CancelEventArgs e)
        {
            var device = _lgService.SelectedDevice;

            var eligibleModels = new[] { "B9", "C9", "E9", "W9" };

            var visible = device?.ModelName != null ? eligibleModels.Any(m => device.ModelName.Contains(m)) : false;
            mnuLgOLEDMotionPro.Visible = visible;
            miLgExpertSeparator1.Visible = visible;

            // Does not work yet, getting a "401 no permissions" error
            //var task = device?.GetPictureSettings();
            //var settings = Utils.WaitForTask<dynamic>(task);

            if (device == null)
            {
                return;
            }

            var actions = device.GetInvokableActions();
            var gameBarActions = device.GetInvokableActionsForGameBar();
            var activatedGameBarActions = device.GetActionsForGameBar();

            const string gameBarName = "miGameBar";

            foreach (var action in actions.Where(a => !a.Name.Contains("Hdmi", StringComparison.OrdinalIgnoreCase) && (a.EnumType != null || a.MinValue >= 0 && a.MaxValue > a.MinValue)))
            {
                var menu = Utils.BuildDropDownMenuEx(mnuLgExpert, action.Title, action.EnumType, btnLgExpertColorGamut_Click, action.Name, (int)action.MinValue, (int)action.MaxValue);

                if (!gameBarActions.Contains(action))
                {
                    continue;
                }

                var itemName = $"{menu.Name}_{gameBarName}";
                var gameBarItem = (ToolStripMenuItem)menu.DropDownItems.Find(itemName, false).FirstOrDefault();

                if (gameBarItem == null)
                {
                    var separator = new ToolStripSeparator();
                    menu.DropDownItems.Add(separator);

                    gameBarItem = (ToolStripMenuItem)menu.DropDownItems.Add("Show in Game Bar", null, miLgGameBarToggle_Click);
                    gameBarItem.CheckOnClick = true;
                    gameBarItem.Checked = activatedGameBarActions.Contains(action);
                    gameBarItem.Name = itemName;
                    gameBarItem.Tag = action.Name;
                }
            }

            if (!_lgService.Config.ShowAdvancedActions)
            {
                return;
            }

            var presetActions = actions.Where(a => a.Preset != null);

            if (presetActions.Any() && mnuLgExpert.Items.Find("miLgExpertActionsSeparator", false).Length == 0)
            {
                mnuLgExpert.Items.Add(new ToolStripSeparator
                {
                    Name = "miLgExpertActionsSeparator"
                });
            }

            foreach (var presetAction in presetActions)
            {
                var menu = Utils.BuildDropDownMenuEx(mnuLgExpert, presetAction.Name, null, btnLgExpertPresetAction_Click, presetAction.Preset);
            }
        }

        private void miLgGameBarToggle_Click(object sender, EventArgs e)
        {
            var device = _lgService.SelectedDevice;

            if (device == null)
            {
                return;
            }

            var item = sender as ToolStripMenuItem;
            if (item.Checked)
            {
                device.AddGameBarAction((string)item.Tag);
            }
            else
            {
                device.RemoveGameBarAction((string)item.Tag);
            }
        }

        private void btnLgExpert_Click(object sender, EventArgs e)
        {
            mnuLgExpert.Show(btnLgExpert, btnLgExpert.PointToClient(Cursor.Position));
        }

        private void btnLgExpertColorGamut_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripItem;
            var name = item.Tag.ToString();
            var value = item.Text;

            _lgService.SelectedDevice?.SetSystemSettings(name, value);
        }

        private void btnLgExpertPresetAction_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripItem;
            var preset = (LgPreset)item.Tag;

            _lgService.ApplyPreset(preset).ConfigureAwait(false);
        }

        private void miLgEnableMotionPro_Click(object sender, EventArgs e)
        {
            if (MessageForms.QuestionYesNo("Are you sure you want to enable OLED Motion Pro? This app and its creator are in no way accountable for any damages it may cause to your tv.") == DialogResult.Yes)
            {
                _lgService.SelectedDevice?.SetOLEDMotionPro("OLED Motion Pro");

                MessageForms.InfoOk("Setting applied.");
            }
        }

        private void miLgDisableMotionPro_Click(object sender, EventArgs e)
        {
            _lgService.SelectedDevice?.SetOLEDMotionPro("OLED Motion");

            MessageForms.InfoOk("Setting applied.");
        }

        private void cbxLgApps_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == ((char)Keys.Back))
            {
                cbxLgApps.SelectedIndex = -1;
            }
        }

        private void btnLgPresetEditTriggerConditions_Click(object sender, EventArgs e)
        {
            var preset = GetSelectedLgPreset();

            if (preset == null)
            {
                return;
            }

            var dropDownValues = Utils.GetDescriptions<PresetConditionType>(fromValue: 1);

            var values = MessageForms.ShowDialog("Set trigger conditions", new[] {
                    new MessageForms.FieldDefinition
                    {
                        Label = "Set desired trigger conditions",
                        FieldType = MessageForms.FieldType.Flags,
                        Values = dropDownValues,
                        Value = edtLgPresetTriggerConditions.Tag ?? 0
                    }
                });

            if (!values.Any())
            {
                return;
            }

            var value = (PresetConditionType)values.First().Value;
            edtLgPresetTriggerConditions.Tag = (PresetConditionType)values.First().Value;
            edtLgPresetTriggerConditions.Text = Utils.GetDescriptions<PresetConditionType>((int)value).Join(", ");
        }

        private void chkCheckForUpdates_CheckedChanged(object sender, EventArgs e)
        {
            if (_initialized)
            {
                _config.CheckForUpdates = chkCheckForUpdates.Checked;
            }
        }

        private void chkLgShowAdvancedActions_CheckedChanged(object sender, EventArgs e)
        {
            if (!_initialized)
            {
                return;
            }

            if (chkLgShowAdvancedActions.Checked)
            {
                if (MessageForms.QuestionYesNo(
@"Are you sure you want to enable the advanced actions under the Expert-button?
These actions include:
- InStart service menu
- Software Update-app with firmware downgrade functionality enabled

These features may cause irreversible damage to your tv and will void your warranty.
This app and its creator are in no way accountable for any damages it may cause to your tv."
                ) != DialogResult.Yes)
                {
                    chkLgShowAdvancedActions.Checked = false;
                    return;
                }
                MessageForms.InfoOk(
@"Advanced actions activated.
The InStart and Software Update items are now visible under the Expert-button."
                );
            }

            _lgService.Config.ShowAdvancedActions = chkLgShowAdvancedActions.Checked;
        }

        private void ToggleGameBar()
        {
            if (_gameBarForm == null || !_gameBarForm.Visible)
            {
                if (_gameBarForm == null || _gameBarForm.IsDisposed)
                {
                    _gameBarForm = new LgGameBar(_lgService);
                }

                _gameBarForm.Show();
                _gameBarForm.Activate();
            }
            else
            {
                _gameBarForm?.Hide();
            }
        }

        private void btnLgGameBar_Click(object sender, EventArgs e)
        {
            ToggleGameBar();
        }

        private void edtLgGameBarShortcut_KeyDown(object sender, KeyEventArgs e)
        {
            ((TextBox)sender).Text = Utils.FormatKeyboardShortcut(e);
        }

        private void edtLgGameBarShortcut_KeyUp(object sender, KeyEventArgs e)
        {
            Utils.HandleKeyboardShortcutUp(e);
        }

        private void edtLgGameBarShortcut_TextChanged(object sender, EventArgs e)
        {
            var text = edtLgGameBarShortcut.Text;

            var blnOk = string.IsNullOrEmpty(text) || !ShortCutExists(text);
            edtLgGameBarShortcut.ForeColor = blnOk ? Color.Red : SystemColors.WindowText;

            if (blnOk)
            {
                _lgService.Config.GameBarShortcut = text;

                if (string.IsNullOrEmpty(text))
                {
                    Utils.UnregisterHotKey(Handle, SHORTCUTID_GAMEBAR);
                }
                else
                {
                    Utils.RegisterShortcut(Handle, SHORTCUTID_GAMEBAR, text);
                }
            }
        }

        private void chkGdiScaling_CheckedChanged(object sender, EventArgs e)
        {
            _config.UseGdiScaling = chkGdiScaling.Checked;
        }

        private void MainForm_ResizeBegin(object sender, EventArgs e)
        {
            SuspendLayout();
        }

        private void MainForm_ResizeEnd(object sender, EventArgs e)
        {
            ResumeLayout(true);
        }
    }
}