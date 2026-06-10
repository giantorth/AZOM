//------------------------------------------------------------------------------
// Strongly-typed accessor over MozaPlugin.Resources.Strings. Each property
// returns the localized string for its key via a per-culture ResourceManager +
// the ambient Thread.CurrentUICulture. Adding a new key: append a one-line
// property below AND add matching <data name="..."/> entries to every
// Strings.*.resx file. Adding a new culture: drop Strings.<lang>.resx in
// Resources/, add a row to _byCulture below, add an EmbeddedResource entry in
// MozaPlugin.csproj (WithCulture=false), and update LanguageResolver's
// SupportedCultures + DisplayNames.
//------------------------------------------------------------------------------
using System.Collections.Generic;
using System.Globalization;
using System.Resources;

namespace MozaPlugin.Resources
{
    /// <summary>
    /// Strongly-typed accessor for localized strings. All locales are embedded
    /// inside the main MozaPlugin.dll (no per-culture satellite assemblies) so
    /// the plugin ships as a single file. Each row in <see cref="_byCulture"/>
    /// maps a BCP-47 tag to a <see cref="ResourceManager"/> reading one of the
    /// embedded <c>.resources</c> blobs; <see cref="Get"/> walks the current UI
    /// culture's parent chain looking for a match, falling back to neutral
    /// (English) if nothing matches.
    /// </summary>
    public static class Strings
    {
        // Each ResourceManager wraps a single embedded .resources blob in the
        // main DLL. The baseName matches the ManifestResourceName set in
        // MozaPlugin.csproj for each Strings.*.resx file.
        private static readonly Dictionary<string, ResourceManager> _byCulture =
            new Dictionary<string, ResourceManager>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "",   new ResourceManager("MozaPlugin.Resources.Strings",    typeof(Strings).Assembly) },
                { "de", new ResourceManager("MozaPlugin.Resources.Strings.de", typeof(Strings).Assembly) },
                { "el", new ResourceManager("MozaPlugin.Resources.Strings.el", typeof(Strings).Assembly) },
                { "es", new ResourceManager("MozaPlugin.Resources.Strings.es", typeof(Strings).Assembly) },
                { "fr", new ResourceManager("MozaPlugin.Resources.Strings.fr", typeof(Strings).Assembly) },
                { "it", new ResourceManager("MozaPlugin.Resources.Strings.it", typeof(Strings).Assembly) },
                { "nb", new ResourceManager("MozaPlugin.Resources.Strings.nb", typeof(Strings).Assembly) },
                { "ru", new ResourceManager("MozaPlugin.Resources.Strings.ru", typeof(Strings).Assembly) },
                { "vi", new ResourceManager("MozaPlugin.Resources.Strings.vi", typeof(Strings).Assembly) },
                { "zh-Hans", new ResourceManager("MozaPlugin.Resources.Strings.zh-Hans", typeof(Strings).Assembly) },
            };

        private static string Get(string key)
        {
            // Walk CurrentUICulture's parent chain. "es-MX" tries "es-MX", then
            // "es", then "" (invariant). Pass InvariantCulture to GetString so
            // the ResourceManager reads from the resource we constructed it
            // with rather than doing its own (satellite-based) culture lookup.
            for (var c = CultureInfo.CurrentUICulture;
                 c != null && !string.IsNullOrEmpty(c.Name);
                 c = c.Parent)
            {
                if (_byCulture.TryGetValue(c.Name, out var rm))
                {
                    var s = rm.GetString(key, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                if (c.Equals(CultureInfo.InvariantCulture)) break;
            }
            return _byCulture[""].GetString(key, CultureInfo.InvariantCulture) ?? key;
        }

        public static string TabHeader_Base => Get("TabHeader_Base");
        public static string TabHeader_Wheel => Get("TabHeader_Wheel");
        public static string TabHeader_Handbrake => Get("TabHeader_Handbrake");
        public static string TabHeader_Pedals => Get("TabHeader_Pedals");
        public static string TabHeader_Ab9Shifter => Get("TabHeader_Ab9Shifter");
        public static string TabHeader_MBooster => Get("TabHeader_MBooster");
        public static string TabHeader_Hub => Get("TabHeader_Hub");
        public static string TabHeader_Options => Get("TabHeader_Options");
        public static string TabHeader_Upload => Get("TabHeader_Upload");
        public static string TabHeader_WheelFiles => Get("TabHeader_WheelFiles");
        public static string TabHeader_Sdk => Get("TabHeader_Sdk");
        public static string TabHeader_Import => Get("TabHeader_Import");
        public static string TabHeader_About => Get("TabHeader_About");
        public static string Status_Disconnected => Get("Status_Disconnected");
        public static string Status_Connected => Get("Status_Connected");
        public static string Status_Recovering => Get("Status_Recovering");
        public static string Status_Parked => Get("Status_Parked");
        public static string Status_TelemetryParked => Get("Status_TelemetryParked");
        public static string Status_DegradedScreenless => Get("Status_DegradedScreenless");
        public static string Button_Refresh => Get("Button_Refresh");
        public static string Label_PerformanceOutput => Get("Label_PerformanceOutput");
        public static string Option_Reserved => Get("Option_Reserved");
        public static string Option_Full => Get("Option_Full");
        public static string Button_CalibrateCenter => Get("Button_CalibrateCenter");
        public static string Brand_Mcu => Get("Brand_Mcu");
        public static string Brand_Mosfet => Get("Brand_Mosfet");
        public static string Brand_Motor => Get("Brand_Motor");
        public static string Section_CoreSettings => Get("Section_CoreSettings");
        public static string Section_GearshiftVibration => Get("Section_GearshiftVibration");
        public static string SliderLabel_WheelRotationAngle => Get("SliderLabel_WheelRotationAngle");
        public static string SliderLabel_GameFfbStrength => Get("SliderLabel_GameFfbStrength");
        public static string SliderLabel_BaseTorqueOutput => Get("SliderLabel_BaseTorqueOutput");
        public static string SliderLabel_MaximumWheelSpeed => Get("SliderLabel_MaximumWheelSpeed");
        public static string SliderLabel_ShiftIntensity => Get("SliderLabel_ShiftIntensity");
        public static string SliderLabel_VibrateOnNeutral => Get("SliderLabel_VibrateOnNeutral");
        public static string SliderLabel_ShiftDebounce => Get("SliderLabel_ShiftDebounce");
        public static string Section_WheelbaseEffects => Get("Section_WheelbaseEffects");
        public static string Section_GameEffects => Get("Section_GameEffects");
        public static string SliderLabel_WheelDamper => Get("SliderLabel_WheelDamper");
        public static string SliderLabel_WheelFriction => Get("SliderLabel_WheelFriction");
        public static string SliderLabel_NaturalInertia => Get("SliderLabel_NaturalInertia");
        public static string SliderLabel_WheelSpring => Get("SliderLabel_WheelSpring");
        public static string SliderLabel_GameDamper => Get("SliderLabel_GameDamper");
        public static string SliderLabel_GameFriction => Get("SliderLabel_GameFriction");
        public static string SliderLabel_GameInertia => Get("SliderLabel_GameInertia");
        public static string SliderLabel_GameSpring => Get("SliderLabel_GameSpring");
        public static string Section_Protection => Get("Section_Protection");
        public static string Section_SoftLimit => Get("Section_SoftLimit");
        public static string SliderLabel_HandsOffProtection => Get("SliderLabel_HandsOffProtection");
        public static string SliderLabel_SteeringWheelInertia => Get("SliderLabel_SteeringWheelInertia");
        public static string SliderLabel_Stiffness => Get("SliderLabel_Stiffness");
        public static string SliderLabel_RetainGameFfb => Get("SliderLabel_RetainGameFfb");
        public static string Section_FfbEqualizer => Get("Section_FfbEqualizer");
        public static string Subtitle_FfbEqualizer => Get("Subtitle_FfbEqualizer");
        public static string Section_FfbOutputCurve => Get("Section_FfbOutputCurve");
        public static string Subtitle_FfbOutputCurve => Get("Subtitle_FfbOutputCurve");
        public static string Button_Flat => Get("Button_Flat");
        public static string Button_Falloff => Get("Button_Falloff");
        public static string Button_Linear => Get("Button_Linear");
        public static string Button_SCurve => Get("Button_SCurve");
        public static string Button_Exponential => Get("Button_Exponential");
        public static string Button_Parabolic => Get("Button_Parabolic");
        public static string Section_Miscellaneous => Get("Section_Miscellaneous");
        public static string Section_HighSpeedDamping => Get("Section_HighSpeedDamping");
        public static string Subtitle_HighSpeedDamping => Get("Subtitle_HighSpeedDamping");
        public static string SliderLabel_ForceFeedbackReversal => Get("SliderLabel_ForceFeedbackReversal");
        public static string SliderLabel_StandbyMode => Get("SliderLabel_StandbyMode");
        public static string SliderLabel_BaseStatusLed => Get("SliderLabel_BaseStatusLed");
        public static string SliderLabel_Bluetooth => Get("SliderLabel_Bluetooth");
        public static string SliderLabel_DampingLevel => Get("SliderLabel_DampingLevel");
        public static string SliderLabel_TriggerSpeed => Get("SliderLabel_TriggerSpeed");
        public static string Section_Paddles => Get("Section_Paddles");
        public static string Label_LeftPaddle => Get("Label_LeftPaddle");
        public static string Label_RightPaddle => Get("Label_RightPaddle");
        public static string Label_Combined => Get("Label_Combined");
        public static string Section_Buttons => Get("Section_Buttons");
        public static string Section_PaddleSettings => Get("Section_PaddleSettings");
        public static string SliderLabel_PaddlesMode => Get("SliderLabel_PaddlesMode");
        public static string Option_Buttons => Get("Option_Buttons");
        public static string Option_Combined => Get("Option_Combined");
        public static string Option_Split => Get("Option_Split");
        public static string SliderLabel_ClutchSplitPoint => Get("SliderLabel_ClutchSplitPoint");
        public static string Section_InputSettings => Get("Section_InputSettings");
        public static string SliderLabel_RotaryEncoders => Get("SliderLabel_RotaryEncoders");
        public static string Option_Knob => Get("Option_Knob");
        public static string SliderLabel_Rotary1 => Get("SliderLabel_Rotary1");
        public static string SliderLabel_Rotary2 => Get("SliderLabel_Rotary2");
        public static string SliderLabel_Rotary3 => Get("SliderLabel_Rotary3");
        public static string SliderLabel_Rotary4 => Get("SliderLabel_Rotary4");
        public static string SliderLabel_Rotary5 => Get("SliderLabel_Rotary5");
        public static string SliderLabel_StickAsDpad => Get("SliderLabel_StickAsDpad");
        public static string SliderLabel_JoystickAssignment => Get("SliderLabel_JoystickAssignment");
        public static string Option_None => Get("Option_None");
        public static string Option_Left => Get("Option_Left");
        public static string Option_Right => Get("Option_Right");
        public static string Hint_LedButtonSettingsInDeviceTab => Get("Hint_LedButtonSettingsInDeviceTab");
        public static string Section_Position => Get("Section_Position");
        public static string Subtitle_LiveHandbrakeInput => Get("Subtitle_LiveHandbrakeInput");
        public static string Label_Position => Get("Label_Position");
        public static string Section_Calibration => Get("Section_Calibration");
        public static string Subtitle_PullHandbrakeFully => Get("Subtitle_PullHandbrakeFully");
        public static string Button_StartCalibration => Get("Button_StartCalibration");
        public static string Button_Stop => Get("Button_Stop");
        public static string Section_HandbrakeSettings => Get("Section_HandbrakeSettings");
        public static string SliderLabel_Mode => Get("SliderLabel_Mode");
        public static string Option_Axis => Get("Option_Axis");
        public static string Option_Button => Get("Option_Button");
        public static string SliderLabel_ButtonThreshold => Get("SliderLabel_ButtonThreshold");
        public static string SliderLabel_ReverseDirection => Get("SliderLabel_ReverseDirection");
        public static string SliderLabel_RangeStart => Get("SliderLabel_RangeStart");
        public static string SliderLabel_RangeEnd => Get("SliderLabel_RangeEnd");
        public static string Section_OutputCurve => Get("Section_OutputCurve");
        public static string Subtitle_MapPhysicalPullToGame => Get("Subtitle_MapPhysicalPullToGame");
        public static string Label_Throttle => Get("Label_Throttle");
        public static string Label_Brake => Get("Label_Brake");
        public static string Label_Clutch => Get("Label_Clutch");
        public static string Section_ThrottleDirectionRange => Get("Section_ThrottleDirectionRange");
        public static string Section_BrakeDirectionRange => Get("Section_BrakeDirectionRange");
        public static string Section_ClutchDirectionRange => Get("Section_ClutchDirectionRange");
        public static string Section_ThrottleOutputCurve => Get("Section_ThrottleOutputCurve");
        public static string Section_BrakeOutputCurve => Get("Section_BrakeOutputCurve");
        public static string Section_ClutchOutputCurve => Get("Section_ClutchOutputCurve");
        public static string Subtitle_MapPhysicalPositionToGame => Get("Subtitle_MapPhysicalPositionToGame");
        public static string Subtitle_ShapeBrakeResponse => Get("Subtitle_ShapeBrakeResponse");
        public static string Subtitle_ShapeClutchResponse => Get("Subtitle_ShapeClutchResponse");
        public static string Section_ThrottleCalibration => Get("Section_ThrottleCalibration");
        public static string Section_BrakeCalibration => Get("Section_BrakeCalibration");
        public static string Section_ClutchCalibration => Get("Section_ClutchCalibration");
        public static string SliderLabel_SensorRatio => Get("SliderLabel_SensorRatio");
        public static string Hint_AngleSensorVsLoadCell => Get("Hint_AngleSensorVsLoadCell");
        public static string Section_Ab9ActiveShifter => Get("Section_Ab9ActiveShifter");
        public static string Status_SearchingForAb9 => Get("Status_SearchingForAb9");
        public static string Section_MechanicalLayout => Get("Section_MechanicalLayout");
        public static string Option_Ab9Layout5R1 => Get("Option_Ab9Layout5R1");
        public static string Option_Ab9Layout6R1 => Get("Option_Ab9Layout6R1");
        public static string Option_Ab9Layout6R2 => Get("Option_Ab9Layout6R2");
        public static string Option_Ab9Layout7R1 => Get("Option_Ab9Layout7R1");
        public static string Option_Ab9Layout7R2 => Get("Option_Ab9Layout7R2");
        public static string Option_Sequential => Get("Option_Sequential");
        public static string Section_Feel => Get("Section_Feel");
        public static string Subtitle_PerAxisMechanicalCharacter => Get("Subtitle_PerAxisMechanicalCharacter");
        public static string SliderLabel_MechanicalResistance => Get("SliderLabel_MechanicalResistance");
        public static string SliderLabel_Spring => Get("SliderLabel_Spring");
        public static string SliderLabel_NaturalDamping => Get("SliderLabel_NaturalDamping");
        public static string SliderLabel_NaturalFriction => Get("SliderLabel_NaturalFriction");
        public static string SliderLabel_MaxOutputTorqueLimit => Get("SliderLabel_MaxOutputTorqueLimit");
        public static string Section_EngineVibration => Get("Section_EngineVibration");
        public static string Subtitle_HostRenderedRumble => Get("Subtitle_HostRenderedRumble");
        public static string SliderLabel_Intensity => Get("SliderLabel_Intensity");
        public static string SliderLabel_Frequency => Get("SliderLabel_Frequency");
        public static string Section_GearShift => Get("Section_GearShift");
        public static string SliderLabel_VibrationIntensity => Get("SliderLabel_VibrationIntensity");
        public static string Section_MBoosterPedals => Get("Section_MBoosterPedals");
        public static string Subtitle_MBoosterPedals => Get("Subtitle_MBoosterPedals");
        public static string SliderLabel_Device => Get("SliderLabel_Device");
        public static string Status_NoMBooster => Get("Status_NoMBooster");
        public static string Section_PedalRole => Get("Section_PedalRole");
        public static string SliderLabel_Role => Get("SliderLabel_Role");
        public static string Option_Disabled => Get("Option_Disabled");
        public static string Option_Throttle => Get("Option_Throttle");
        public static string Option_Brake => Get("Option_Brake");
        public static string Option_Clutch => Get("Option_Clutch");
        public static string Section_Effects => Get("Section_Effects");
        public static string Subtitle_EffectsTriggers => Get("Subtitle_EffectsTriggers");
        public static string Section_Abs => Get("Section_Abs");
        public static string Label_Enable => Get("Label_Enable");
        public static string Button_Test1s => Get("Button_Test1s");
        public static string Section_Lockup => Get("Section_Lockup");
        public static string Section_Threshold => Get("Section_Threshold");
        public static string Section_EngineContinuous => Get("Section_EngineContinuous");
        public static string Subtitle_CalibrationExperimental => Get("Subtitle_CalibrationExperimental");
        public static string Label_ReversedDirection => Get("Label_ReversedDirection");
        public static string SliderLabel_MinRaw => Get("SliderLabel_MinRaw");
        public static string SliderLabel_MaxRaw => Get("SliderLabel_MaxRaw");
        public static string Button_ReadFromDevice => Get("Button_ReadFromDevice");
        public static string Button_Apply => Get("Button_Apply");
        public static string Section_UniversalHubPedals => Get("Section_UniversalHubPedals");
        public static string Subtitle_PedalsPort => Get("Subtitle_PedalsPort");
        public static string Label_PedalsPort => Get("Label_PedalsPort");
        public static string Section_Accessories => Get("Section_Accessories");
        public static string Label_Port1 => Get("Label_Port1");
        public static string Label_Port2 => Get("Label_Port2");
        public static string Label_Port3 => Get("Label_Port3");
        public static string Section_Profiles => Get("Section_Profiles");
        public static string SliderLabel_ApplyProfileOnLaunch => Get("SliderLabel_ApplyProfileOnLaunch");
        public static string Section_WheelLedOutput => Get("Section_WheelLedOutput");
        public static string Subtitle_WheelLedOutput => Get("Subtitle_WheelLedOutput");
        public static string Hint_LedOptionsRecommendedOff => Get("Hint_LedOptionsRecommendedOff");
        public static string SliderLabel_LimitWheelUpdates => Get("SliderLabel_LimitWheelUpdates");
        public static string Hint_OnlySendLedWhenChanged => Get("Hint_OnlySendLedWhenChanged");
        public static string SliderLabel_AlwaysResendBitmask => Get("SliderLabel_AlwaysResendBitmask");
        public static string Hint_AlwaysResendBitmask => Get("Hint_AlwaysResendBitmask");
        public static string Section_UsbDetection => Get("Section_UsbDetection");
        public static string SliderLabel_DisableSerialProbe => Get("SliderLabel_DisableSerialProbe");
        public static string Hint_DisableSerialProbe => Get("Hint_DisableSerialProbe");
        public static string SliderLabel_DisableAb9Detection => Get("SliderLabel_DisableAb9Detection");
        public static string Hint_DisableAb9Detection => Get("Hint_DisableAb9Detection");
        public static string Section_DashboardTelemetry => Get("Section_DashboardTelemetry");
        public static string Label_UploadDashboardOnConnect => Get("Label_UploadDashboardOnConnect");
        public static string Label_DownloadDashboardsFromWheel => Get("Label_DownloadDashboardsFromWheel");
        public static string Label_WheelFirmwareEra => Get("Label_WheelFirmwareEra");
        public static string Option_FirmwareEraAuto => Get("Option_FirmwareEraAuto");
        public static string Option_FirmwareEra2024 => Get("Option_FirmwareEra2024");
        public static string Option_FirmwareEra2026 => Get("Option_FirmwareEra2026");
        public static string Hint_FirmwareEra => Get("Hint_FirmwareEra");
        public static string Section_Reset => Get("Section_Reset");
        public static string Button_ClearAllSettings => Get("Button_ClearAllSettings");
        public static string Hint_ClearAllSettingsWarning => Get("Hint_ClearAllSettingsWarning");
        public static string Section_Language => Get("Section_Language");
        public static string SliderLabel_Language => Get("SliderLabel_Language");
        public static string Hint_LanguageChangeRestart => Get("Hint_LanguageChangeRestart");
        public static string Banner_NotWorkingYet => Get("Banner_NotWorkingYet");
        public static string Hint_DashboardUploadNotWorking => Get("Hint_DashboardUploadNotWorking");
        public static string Section_DashboardUpload => Get("Section_DashboardUpload");
        public static string Subtitle_DashboardUpload => Get("Subtitle_DashboardUpload");
        public static string Hint_UploadStatusFormat => Get("Hint_UploadStatusFormat");
        public static string Label_Source => Get("Label_Source");
        public static string Option_LocalMzdashFile => Get("Option_LocalMzdashFile");
        public static string Option_DashboardLibrary => Get("Option_DashboardLibrary");
        public static string Button_PickMzdash => Get("Button_PickMzdash");
        public static string Status_NoFileSelected => Get("Status_NoFileSelected");
        public static string Label_Dashboard => Get("Label_Dashboard");
        public static string Button_UploadNow => Get("Button_UploadNow");
        public static string Status_Idle => Get("Status_Idle");
        public static string Label_DashboardName => Get("Label_DashboardName");
        public static string Label_RawSize => Get("Label_RawSize");
        public static string Label_Md5 => Get("Label_Md5");
        public static string Label_InFlight => Get("Label_InFlight");
        public static string Label_LastAckBytes => Get("Label_LastAckBytes");
        public static string Label_LastAckStatus => Get("Label_LastAckStatus");
        public static string Hint_UploadRequiresConnection => Get("Hint_UploadRequiresConnection");
        public static string Section_WheelFiles => Get("Section_WheelFiles");
        public static string Subtitle_WheelFiles => Get("Subtitle_WheelFiles");
        public static string Hint_DeleteDisabled => Get("Hint_DeleteDisabled");
        public static string DataGridHeader_State => Get("DataGridHeader_State");
        public static string DataGridHeader_Title => Get("DataGridHeader_Title");
        public static string DataGridHeader_DirName => Get("DataGridHeader_DirName");
        public static string DataGridHeader_Hash => Get("DataGridHeader_Hash");
        public static string DataGridHeader_LastModified => Get("DataGridHeader_LastModified");
        public static string Button_Delete => Get("Button_Delete");
        public static string Tooltip_DeleteDisabled => Get("Tooltip_DeleteDisabled");
        public static string Section_CoapServer => Get("Section_CoapServer");
        public static string Subtitle_CoapServer => Get("Subtitle_CoapServer");
        public static string Hint_CoapServerEnabled => Get("Hint_CoapServerEnabled");
        public static string Hint_CoapStubProcessName => Get("Hint_CoapStubProcessName");
        public static string SliderLabel_EnableCoapServer => Get("SliderLabel_EnableCoapServer");
        public static string Section_UdpControl => Get("Section_UdpControl");
        public static string Subtitle_UdpControl => Get("Subtitle_UdpControl");
        public static string Hint_UdpControlEnabled => Get("Hint_UdpControlEnabled");
        public static string SliderLabel_EnableUdpControl => Get("SliderLabel_EnableUdpControl");
        public static string Label_PortNumber => Get("Label_PortNumber");
        public static string Section_Status => Get("Section_Status");
        public static string Label_CoapServer => Get("Label_CoapServer");
        public static string Label_UdpControlServer => Get("Label_UdpControlServer");
        public static string Section_RecentRequests => Get("Section_RecentRequests");
        public static string Subtitle_RecentRequests => Get("Subtitle_RecentRequests");
        public static string Section_About => Get("Section_About");
        public static string Subtitle_About => Get("Subtitle_About");
        public static string Label_AzomTitle => Get("Label_AzomTitle");
        public static string Label_VersionPlaceholder => Get("Label_VersionPlaceholder");
        public static string Hint_AboutDescription => Get("Hint_AboutDescription");
        public static string Hint_SponsorshipsAppreciated => Get("Hint_SponsorshipsAppreciated");
        public static string Hint_ThanksToTesters => Get("Hint_ThanksToTesters");
        public static string Button_Github => Get("Button_Github");
        public static string Button_JoinDiscord => Get("Button_JoinDiscord");
        public static string Button_SponsorDevelopment => Get("Button_SponsorDevelopment");
        public static string Tooltip_Github => Get("Tooltip_Github");
        public static string Tooltip_JoinDiscord => Get("Tooltip_JoinDiscord");
        public static string Tooltip_Sponsor => Get("Tooltip_Sponsor");
        public static string Label_UpdateAvailable => Get("Label_UpdateAvailable");
        public static string Button_OpenReleaseNotes => Get("Button_OpenReleaseNotes");
        public static string Button_SkipThisVersion => Get("Button_SkipThisVersion");
        public static string Button_DismissUpdate => Get("Button_DismissUpdate");
        public static string Banner_SdkSuggestionText => Get("Banner_SdkSuggestionText");
        public static string Button_ConfigureSdk => Get("Button_ConfigureSdk");
        public static string Button_Dismiss => Get("Button_Dismiss");
        public static string Button_RestartSimHub => Get("Button_RestartSimHub");
        public static string Label_WhatsNew => Get("Label_WhatsNew");
        public static string Label_CheckForUpdates => Get("Label_CheckForUpdates");
        public static string Label_ReleaseChannel => Get("Label_ReleaseChannel");
        public static string Option_ReleaseChannelStable => Get("Option_ReleaseChannelStable");
        public static string Option_ReleaseChannelDev => Get("Option_ReleaseChannelDev");
        public static string Button_CheckNow => Get("Button_CheckNow");
        public static string Status_UpdateNeverChecked => Get("Status_UpdateNeverChecked");
        public static string Status_UpdateChecking => Get("Status_UpdateChecking");
        public static string Status_UpdateUpToDate => Get("Status_UpdateUpToDate");
        public static string Status_UpdateFailedNetwork => Get("Status_UpdateFailedNetwork");
        public static string Status_UpdateFailedHttp => Get("Status_UpdateFailedHttp");
        public static string Status_UpdateFailedParse => Get("Status_UpdateFailedParse");
        public static string Button_InstallUpdate => Get("Button_InstallUpdate");
        public static string Status_DownloadingStart => Get("Status_DownloadingStart");
        public static string Status_Downloading => Get("Status_Downloading");
        public static string Status_DownloadingIndeterminate => Get("Status_DownloadingIndeterminate");
        public static string Status_Extracting => Get("Status_Extracting");
        public static string Status_Installing => Get("Status_Installing");
        public static string Status_InstalledRestartRequired => Get("Status_InstalledRestartRequired");
        public static string Status_InstallFailed => Get("Status_InstallFailed");
        public static string Status_InstallFailedBadPackage => Get("Status_InstallFailedBadPackage");
        public static string Status_InstallFailedPendingRestart => Get("Status_InstallFailedPendingRestart");
        public static string Status_InstallFailedWriteDenied => Get("Status_InstallFailedWriteDenied");
        public static string Section_Updates => Get("Section_Updates");
        public static string Subtitle_Updates => Get("Subtitle_Updates");
        public static string Section_Bandwidth => Get("Section_Bandwidth");
        public static string Subtitle_Bandwidth => Get("Subtitle_Bandwidth");
        public static string Label_Inbound => Get("Label_Inbound");
        public static string Label_Outbound => Get("Label_Outbound");
        public static string Label_Capacity => Get("Label_Capacity");
        public static string Label_Peak => Get("Label_Peak");
        public static string Label_Session => Get("Label_Session");
        public static string Section_SerialTrafficCapture => Get("Section_SerialTrafficCapture");
        public static string Subtitle_SerialTrafficCapture => Get("Subtitle_SerialTrafficCapture");
        public static string Hint_CaptureDataInMemory => Get("Hint_CaptureDataInMemory");
        public static string Button_StartCapture => Get("Button_StartCapture");
        public static string Label_AlwaysCaptureOnStartup => Get("Label_AlwaysCaptureOnStartup");
        public static string Tooltip_AlwaysCaptureOnStartup => Get("Tooltip_AlwaysCaptureOnStartup");
        public static string Button_ExportBundle => Get("Button_ExportBundle");
        public static string Button_CopyCapture => Get("Button_CopyCapture");
        public static string Section_FullDiagReport => Get("Section_FullDiagReport");
        public static string Subtitle_FullDiagReport => Get("Subtitle_FullDiagReport");
        public static string Button_Expand => Get("Button_Expand");
        public static string Button_Collapse => Get("Button_Collapse");
        public static string Button_CopyAll => Get("Button_CopyAll");
        public static string Hint_ClickExpandToRender => Get("Hint_ClickExpandToRender");
        public static string Hint_FullDiagRendered => Get("Hint_FullDiagRendered");
        public static string Hint_FullDiagRenderFailed => Get("Hint_FullDiagRenderFailed");
        public static string Hint_MBoosterIntro => Get("Hint_MBoosterIntro");
        public static string Hint_MBoosterCalibrationWarning => Get("Hint_MBoosterCalibrationWarning");

        // ----- ColorPickerDialog
        public static string Title_PickLedColor => Get("Title_PickLedColor");
        public static string Label_Palette => Get("Label_Palette");
        public static string Label_Last => Get("Label_Last");
        public static string Label_FineTuneRgb => Get("Label_FineTuneRgb");
        public static string Button_Cancel => Get("Button_Cancel");
        public static string Button_Ok => Get("Button_Ok");

        // ----- MozaBaseSettingsControl
        public static string Section_BaseAmbientLeds => Get("Section_BaseAmbientLeds");
        public static string Label_BaseModel => Get("Label_BaseModel");
        public static string Hint_NoBaseAmbientDetected => Get("Hint_NoBaseAmbientDetected");
        public static string Section_Mode => Get("Section_Mode");
        public static string SliderLabel_IndicatorState => Get("SliderLabel_IndicatorState");
        public static string Option_Off => Get("Option_Off");
        public static string Option_On => Get("Option_On");
        public static string SliderLabel_StandbyAnimation => Get("SliderLabel_StandbyAnimation");
        public static string Option_Constant => Get("Option_Constant");
        public static string Option_Breath => Get("Option_Breath");
        public static string Option_Cycle => Get("Option_Cycle");
        public static string Option_Rainbow => Get("Option_Rainbow");
        public static string Option_Flow => Get("Option_Flow");
        public static string Section_Brightness => Get("Section_Brightness");
        public static string SliderLabel_Brightness => Get("SliderLabel_Brightness");
        public static string Section_Sleep => Get("Section_Sleep");
        public static string SliderLabel_SleepMode => Get("SliderLabel_SleepMode");
        public static string Option_Enabled => Get("Option_Enabled");
        public static string SliderLabel_SleepTimeout => Get("SliderLabel_SleepTimeout");
        public static string Section_PowerOnOffColor => Get("Section_PowerOnOffColor");
        public static string SliderLabel_StartupColor => Get("SliderLabel_StartupColor");
        public static string SliderLabel_ShutdownColor => Get("SliderLabel_ShutdownColor");

        // ----- MozaDashSettingsControl
        public static string Section_MozaDashboard => Get("Section_MozaDashboard");
        public static string Status_SearchingForDashboard => Get("Status_SearchingForDashboard");
        public static string Section_IndicatorModes => Get("Section_IndicatorModes");
        public static string SliderLabel_RpmIndicatorMode => Get("SliderLabel_RpmIndicatorMode");
        public static string Option_SimHubMode => Get("Option_SimHubMode");
        public static string Option_AlwaysOn => Get("Option_AlwaysOn");
        public static string SliderLabel_RpmDisplayMode => Get("SliderLabel_RpmDisplayMode");
        public static string Option_Mode1 => Get("Option_Mode1");
        public static string Option_Mode2 => Get("Option_Mode2");
        public static string SliderLabel_FlagsIndicatorMode => Get("SliderLabel_FlagsIndicatorMode");
        public static string SliderLabel_RpmBrightness => Get("SliderLabel_RpmBrightness");
        public static string SliderLabel_FlagsBrightness => Get("SliderLabel_FlagsBrightness");
        public static string Section_RpmLedColors => Get("Section_RpmLedColors");
        public static string Section_RpmBlinkColors => Get("Section_RpmBlinkColors");
        public static string Subtitle_RpmBlinkColors => Get("Subtitle_RpmBlinkColors");
        public static string Section_FlagLedColors => Get("Section_FlagLedColors");

        // ----- Themes/Generic.xaml (custom-control templates)
        public static string Label_SteeringAngle => Get("Label_SteeringAngle");
        public static string Toggle_Off => Get("Toggle_Off");
        public static string Toggle_On => Get("Toggle_On");

        // ----- MozaWheelSettingsControl
        public static string TabHeader_Inputs => Get("TabHeader_Inputs");
        public static string TabHeader_Dashboard => Get("TabHeader_Dashboard");
        public static string TabHeader_Leds => Get("TabHeader_Leds");
        public static string TabHeader_Rpm => Get("TabHeader_Rpm");
        public static string TabHeader_Buttons => Get("TabHeader_Buttons");
        public static string TabHeader_Knobs => Get("TabHeader_Knobs");
        public static string TabHeader_Files => Get("TabHeader_Files");
        public static string TabHeader_Sleep => Get("TabHeader_Sleep");
        public static string Status_SearchingForWheel => Get("Status_SearchingForWheel");
        public static string Section_LivePaddles => Get("Section_LivePaddles");
        public static string Label_PaddleLeft => Get("Label_PaddleLeft");
        public static string Label_PaddleRight => Get("Label_PaddleRight");
        public static string Label_PaddleCombined => Get("Label_PaddleCombined");
        public static string Section_ActiveButtons => Get("Section_ActiveButtons");
        public static string Section_Joystick => Get("Section_Joystick");
        public static string Subtitle_JoystickAssignment => Get("Subtitle_JoystickAssignment");
        public static string Hint_NoJoystickConfig => Get("Hint_NoJoystickConfig");
        public static string Label_EnableDashboardTelemetry => Get("Label_EnableDashboardTelemetry");
        public static string Button_LoadMzdash => Get("Button_LoadMzdash");
        public static string Button_SetFolder => Get("Button_SetFolder");
        public static string Button_AutoDetect => Get("Button_AutoDetect");
        public static string Tooltip_LoadMzdash => Get("Tooltip_LoadMzdash");
        public static string Tooltip_SetFolder => Get("Tooltip_SetFolder");
        public static string Tooltip_AutoDetect => Get("Tooltip_AutoDetect");
        public static string Button_SendTestPattern => Get("Button_SendTestPattern");
        public static string Button_StopTest => Get("Button_StopTest");
        public static string Section_ChannelMappings => Get("Section_ChannelMappings");
        public static string Hint_ClickPencilForProperty => Get("Hint_ClickPencilForProperty");
        public static string Status_LoadingChannelMappings => Get("Status_LoadingChannelMappings");
        public static string DataGridHeader_Channel => Get("DataGridHeader_Channel");
        public static string DataGridHeader_SimHubProperty => Get("DataGridHeader_SimHubProperty");
        public static string DataGridHeader_CurrentValue => Get("DataGridHeader_CurrentValue");
        public static string Tooltip_EditMapping => Get("Tooltip_EditMapping");
        public static string Button_ResetToDefaults => Get("Button_ResetToDefaults");
        public static string Section_Display => Get("Section_Display");
        public static string SliderLabel_DisplayBrightness => Get("SliderLabel_DisplayBrightness");
        public static string SliderLabel_StandbyTime => Get("SliderLabel_StandbyTime");
        public static string Option_Time1Min => Get("Option_Time1Min");
        public static string Option_Time2Min => Get("Option_Time2Min");
        public static string Option_Time3Min => Get("Option_Time3Min");
        public static string Option_Time4Min => Get("Option_Time4Min");
        public static string Option_Time5Min => Get("Option_Time5Min");
        public static string Option_Time10Min => Get("Option_Time10Min");
        public static string Option_Time15Min => Get("Option_Time15Min");
        public static string Option_Time20Min => Get("Option_Time20Min");
        public static string Option_Time25Min => Get("Option_Time25Min");
        public static string Option_Time30Min => Get("Option_Time30Min");
        public static string Option_Time35Min => Get("Option_Time35Min");
        public static string Option_Time40Min => Get("Option_Time40Min");
        public static string Option_Time45Min => Get("Option_Time45Min");
        public static string Option_Time1Hour => Get("Option_Time1Hour");
        public static string Option_Time2Hour => Get("Option_Time2Hour");
        public static string Option_Time3Hour => Get("Option_Time3Hour");
        public static string Option_Time4Hour => Get("Option_Time4Hour");
        public static string Option_Time5Hour => Get("Option_Time5Hour");
        public static string Section_RpmLedMode => Get("Section_RpmLedMode");
        public static string SliderLabel_RpmLedMode => Get("SliderLabel_RpmLedMode");
        public static string Option_Static => Get("Option_Static");
        public static string SliderLabel_RpmIdleEffect => Get("SliderLabel_RpmIdleEffect");
        public static string Option_Breathing => Get("Option_Breathing");
        public static string Option_ColorCycle => Get("Option_ColorCycle");
        public static string Option_SandFlow => Get("Option_SandFlow");
        public static string Option_RgbPulse => Get("Option_RgbPulse");
        public static string SliderLabel_RpmIdleSpeed => Get("SliderLabel_RpmIdleSpeed");
        public static string Section_RpmLedColorsStatic => Get("Section_RpmLedColorsStatic");
        public static string Subtitle_RpmLedColorsStatic => Get("Subtitle_RpmLedColorsStatic");
        public static string Label_LedPlaceholder => Get("Label_LedPlaceholder");
        public static string Subtitle_ClickFlagToRecolor => Get("Subtitle_ClickFlagToRecolor");
        public static string Label_FlagPlaceholder => Get("Label_FlagPlaceholder");
        public static string Section_EsWheelRpmIndicator => Get("Section_EsWheelRpmIndicator");
        public static string Subtitle_EsLegacy => Get("Subtitle_EsLegacy");
        public static string SliderLabel_IndicatorMode => Get("SliderLabel_IndicatorMode");
        public static string SliderLabel_DisplayMode => Get("SliderLabel_DisplayMode");
        public static string Section_ButtonLedMode => Get("Section_ButtonLedMode");
        public static string SliderLabel_ButtonLedMode => Get("SliderLabel_ButtonLedMode");
        public static string SliderLabel_ButtonIdleEffect => Get("SliderLabel_ButtonIdleEffect");
        public static string SliderLabel_ButtonIdleSpeed => Get("SliderLabel_ButtonIdleSpeed");
        public static string Section_ButtonLedColors => Get("Section_ButtonLedColors");
        public static string Subtitle_ButtonLedColors => Get("Subtitle_ButtonLedColors");
        public static string Label_ButtonPlaceholder => Get("Label_ButtonPlaceholder");
        public static string Section_KnobLedMode => Get("Section_KnobLedMode");
        public static string SliderLabel_KnobLedMode => Get("SliderLabel_KnobLedMode");
        public static string SliderLabel_KnobIdleEffect => Get("SliderLabel_KnobIdleEffect");
        public static string SliderLabel_KnobIdleSpeed => Get("SliderLabel_KnobIdleSpeed");
        public static string Section_KnobSettings => Get("Section_KnobSettings");
        public static string Label_SignalMode => Get("Label_SignalMode");
        public static string Subtitle_SignalMode => Get("Subtitle_SignalMode");
        public static string SliderLabel_AllRotaries => Get("SliderLabel_AllRotaries");
        public static string Label_Colours => Get("Label_Colours");
        public static string Subtitle_KnobColours => Get("Subtitle_KnobColours");
        public static string Label_KnobEditing => Get("Label_KnobEditing");
        public static string Button_FillRingWithSelected => Get("Button_FillRingWithSelected");
        public static string Button_CopyKnobToAll => Get("Button_CopyKnobToAll");
        public static string Banner_DashboardUploadNotWorkingYet => Get("Banner_DashboardUploadNotWorkingYet");
        public static string Section_SleepLight => Get("Section_SleepLight");
        public static string Subtitle_SleepLight => Get("Subtitle_SleepLight");
        public static string SliderLabel_Speed => Get("SliderLabel_Speed");
        public static string SliderLabel_Color => Get("SliderLabel_Color");

        // PitHouse preset import (Button_ImportProfile + Import_*).
        public static string Button_ImportProfile => Get("Button_ImportProfile");
        public static string Import_DialogTitle => Get("Import_DialogTitle");
        public static string Import_Tab_Browse => Get("Import_Tab_Browse");
        public static string Import_Category_Motor => Get("Import_Category_Motor");
        public static string Import_Category_Pedals => Get("Import_Category_Pedals");
        public static string Import_NoFolderFound => Get("Import_NoFolderFound");
        public static string Import_SetCustomFolder => Get("Import_SetCustomFolder");
        public static string Import_ConfirmHeader => Get("Import_ConfirmHeader");
        public static string Import_NoChangesHeader => Get("Import_NoChangesHeader");
        public static string Import_NotImportedHeader => Get("Import_NotImportedHeader");
        public static string Import_ApplyButton => Get("Import_ApplyButton");
        public static string Import_CancelButton => Get("Import_CancelButton");
        public static string Import_NextButton => Get("Import_NextButton");
        public static string Import_BackButton => Get("Import_BackButton");
        public static string Import_BrowseDescription => Get("Import_BrowseDescription");
        public static string Import_Error_InvalidJson => Get("Import_Error_InvalidJson");
        public static string Import_Error_UnsupportedType => Get("Import_Error_UnsupportedType");
    }
}
