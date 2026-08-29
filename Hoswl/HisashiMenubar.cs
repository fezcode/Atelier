using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Atelier.Hoswl
{
    /// <summary>
    /// Wires the window's menu to Hisashi's menubar: owns the <see cref="HoswlClient"/>,
    /// republishes the tree whenever the view model changes something the menu
    /// reflects (enabled rows, the Image Metadata checkmark), dispatches clicks on
    /// the UI thread, and tells the window when the in-app strip should hide.
    /// </summary>
    public sealed class HisashiMenubar : IDisposable
    {
        public const string AppId = "com.fezcode.atelier";

        /// <summary>Pipe to connect to; tests point it at a name nobody listens on.</summary>
        internal static string PipeName = HoswlClient.DefaultPipeName;

        private readonly Menu _menu;
        private readonly INotifyPropertyChanged? _viewModel;
        private readonly UserSettings _settings;
        private readonly string _version;
        private readonly Dictionary<string, MenuItem> _map = new();
        private HoswlClient? _client;
        private string? _lastJson;
        private bool _connected;
        private bool _publishQueued;
        private bool _disposed;

        public HisashiMenubar(Menu menu, INotifyPropertyChanged? viewModel, UserSettings settings, string version)
        {
            _menu = menu;
            _viewModel = viewModel;
            _settings = settings;
            _version = version;
            if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelChanged;
        }

        /// <summary>Raised on the UI thread whenever <see cref="MenusExternal"/> may have changed.</summary>
        public event Action? StateChanged;

        public UserSettings Settings => _settings;
        public bool Integration => _settings.HisashiIntegration;
        public bool ShowMenus => _settings.HisashiMenus;
        public bool IsConnected => _connected;

        /// <summary>True while Hisashi is showing our menus — the in-app strip should get out of the way.</summary>
        public bool MenusExternal => Integration && ShowMenus && _connected;

        /// <summary>Start or stop the client to match the settings. Safe to call repeatedly.</summary>
        public void Apply()
        {
            if (_disposed) return;
            if (_settings.HisashiIntegration)
            {
                if (_client == null)
                {
                    _client = new HoswlClient(AppId, "Atelier", _version, PipeName);
                    _client.OnClick += id => Dispatcher.UIThread.Post(() => HoswlMenuBuilder.Dispatch(_map, id));
                    _client.ConnectionChanged += up => Dispatcher.UIThread.Post(() => OnConnection(up));
                    _client.SetEnabled(_settings.HisashiMenus);
                    Publish(force: true);
                    _client.Start();
                }
                else
                {
                    _client.SetEnabled(_settings.HisashiMenus);
                }
            }
            else if (_client != null)
            {
                _client.Stop();
                _client.Dispose();
                _client = null;
                _connected = false;
            }
            StateChanged?.Invoke();
        }

        public void SetIntegration(bool on)
        {
            if (_settings.HisashiIntegration == on) return;
            _settings.HisashiIntegration = on;
            _settings.Save();
            Apply();
        }

        public void SetShowMenus(bool on)
        {
            if (_settings.HisashiMenus == on) return;
            _settings.HisashiMenus = on;
            _settings.Save();
            Apply();
        }

        /// <summary>Rebuild the tree from the live menu and send it if it changed.</summary>
        public void Publish(bool force = false)
        {
            if (_disposed) return;
            var json = HoswlMenuBuilder.Build(_menu, _map);
            if (!force && json == _lastJson) return;
            _lastJson = json;
            _client?.SetMenusJson(json);
        }

        /// <summary>The JSON most recently built — for tests and diagnostics.</summary>
        public string? LastJson => _lastJson;

        /// <summary>The click handler Hisashi's <c>click</c> lines end up in (UI thread).</summary>
        public bool Dispatch(string id) => HoswlMenuBuilder.Dispatch(_map, id);

        internal void SetConnectedForTest(bool on) => OnConnection(on);

        private void OnConnection(bool up)
        {
            if (_disposed) return;
            _connected = up;
            if (up) Publish(force: true);
            StateChanged?.Invoke();
        }

        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Bindings on the MenuItems update synchronously on the UI thread, but coalesce
            // a burst of property changes into one publish after they have all applied.
            if (_publishQueued || _client == null) return;
            _publishQueued = true;
            Dispatcher.UIThread.Post(() => { _publishQueued = false; Publish(); }, DispatcherPriority.Background);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _client?.Dispose();
            _client = null;
        }
    }
}
