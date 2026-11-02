using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Extensions.Configuration;

namespace UEVR {
    // Mopic: an --attach launch without --show (Mopic Hub, the test harness) runs this instead of MainWindow, so the
    // injector creates no window at all. It injects the way MainWindow's attach path does, with the settings that
    // window saved, answers the backend's config signal, and exits with the game.
    class AttachSession {
        // A game that never starts must not leave us waiting to inject a later launch of it. Counted in timer ticks
        // (about 1 s each), so time the PC spends asleep doesn't count; Steam updates and launchers can take a while.
        private const int GameWaitLimitTicks = 30 * 60;

        private readonly string m_processName;
        private readonly MainWindowSettings m_settings = new MainWindowSettings();
        private readonly ExecutableFilter m_executableFilter = new ExecutableFilter();
        private readonly DispatcherTimer m_updateTimer = new DispatcherTimer {
            Interval = new TimeSpan(0, 0, 1)
        };

        private int m_waitTicks = 0;
        private int m_attachedProcessId = 0;
        private bool m_connected = false;
        private DateTime m_lastFrontendSignal = DateTime.MinValue;

        public AttachSession(string attachExe) {
            m_processName = attachExe.ToLower().Replace(".exe", "");
        }

        public void Start() {
            Injector.ShowErrors = false;

            m_updateTimer.Tick += (sender, e) => Update();
            m_updateTimer.Start();
        }

        private void Update() {
            try {
                if (m_attachedProcessId == 0) {
                    if (!TryInject() && ++m_waitTicks > GameWaitLimitTicks) {
                        Application.Current.Shutdown();
                    }

                    return;
                }

                if (!IsProcessRunning(m_attachedProcessId)) {
                    Application.Current.Shutdown();
                    return;
                }

                var data = SharedMemory.GetData();
                DateTime now = DateTime.Now;

                // The mapping's name is global: only our game's UEVR counts
                if (data?.pid == m_attachedProcessId) {
                    m_connected = true;

                    if (data?.signalFrontendConfigSetup == true && (now - m_lastFrontendSignal > TimeSpan.FromSeconds(1))) {
                        SharedMemory.SendCommand(SharedMemory.Command.ConfigSetupAcknowledged);
                        m_lastFrontendSignal = now;
                    }

                    return;
                }

                // UEVR went away while the game runs on; MainWindow's attach path exits then too
                if (m_connected) {
                    Application.Current.Shutdown();
                }
            } catch (Exception) {
                // Nobody sees this process: an exception (the game exiting mid-injection) must not crash it
            }
        }

        private bool TryInject() {
            var process = Process.GetProcessesByName(m_processName).LastOrDefault(p => MainWindow.IsInjectableProcess(p, m_executableFilter));

            if (process == null) {
                return false;
            }

            // Set first: when injecting throws, the next tick checks on this game instead of injecting it again
            m_attachedProcessId = process.Id;

            if (m_settings.NullifyVRPluginsCheckbox) {
                IntPtr nullifierBase;
                if (Injector.InjectDll(process.Id, "UEVRPluginNullifier.dll", out nullifierBase) && nullifierBase.ToInt64() > 0) {
                    Injector.CallFunctionNoArgs(process.Id, "UEVRPluginNullifier.dll", nullifierBase, "nullify", true);
                }
            }

            // MainWindow loads its OpenVR radio button first and its OpenXR one second, so OpenXR wins when both were saved
            string runtimeName = m_settings.OpenXRRadio ? "openxr_loader.dll" : "openvr_api.dll";

            if (Injector.InjectDll(process.Id, runtimeName)) {
                RequestRuntime(process.ProcessName, runtimeName);
                Injector.InjectDll(process.Id, "UEVRBackend.dll");
            }

            if (m_settings.FocusGameOnInjection) {
                MainWindow.SwitchToThisWindow(process.MainWindowHandle, true);
            }

            return true;
        }

        // The game's config names the injected runtime before UEVRBackend reads it, as MainWindow's attach path does
        private static void RequestRuntime(string gameName, string runtimeName) {
            try {
                var configPath = MainWindow.GetGlobalGameDir(gameName) + "\\config.txt";
                var config = new ConfigurationBuilder().AddIniFile(configPath, optional: true, reloadOnChange: false).Build();

                foreach (var entry in MandatoryConfig.Entries) {
                    if (config.AsEnumerable().ToList().FindAll(v => v.Key == entry.Key).Count() == 0) {
                        config[entry.Key] = entry.Value;
                    }
                }

                if (config["Frontend_RequestedRuntime"] != runtimeName) {
                    config["Frontend_RequestedRuntime"] = runtimeName;
                    File.WriteAllText(configPath, MainWindow.IniToString(config));
                }
            } catch (Exception) {
            }
        }

        private static bool IsProcessRunning(int processId) {
            try {
                return !Process.GetProcessById(processId).HasExited;
            } catch (ArgumentException) {
                return false;
            } catch (InvalidOperationException) {
                return false;
            } catch (Win32Exception) {
                return true; // no access (an elevated game): can't tell, keep waiting
            }
        }
    }
}
