using AnimeJaNaiConfEditor.Services;
using AnimeJaNaiConfEditor.Services.PlayerSettings;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.ViewModels
{
    // The Player tab: a curated set of mpv options, saved to the user's part of mpv.conf and, when
    // the player opened the Manager (--player-ipc), applied to the running player too.
    public class PlayerSettingsViewModel : ViewModelBase
    {
        private readonly PlayerConfig _config;
        private string _filter = "";
        private string _status = "";
        private bool _statusIsError;
        // one change at a time: each reads the file, edits it and talks to the player
        private Task _applying = Task.CompletedTask;

        public PlayerSettingsViewModel(string portableConfigDir) : this(PlayerConfig.Load(OptionCatalog.Embedded, portableConfigDir)) { }

        public PlayerSettingsViewModel(PlayerConfig config)
        {
            _config = config;
            Sections = new(config.Catalog.Sections.Select(name => new PlayerSettingsSection(name,
                config.Catalog.Options.Where(o => o.Section == name && o.AvailableHere).Select(o => new PlayerOptionViewModel(this, o)))));
            PlayerIpc.EndpointChanged += () => this.RaisePropertyChanged(nameof(LiveText));
            RefreshAll();
        }

        public ObservableCollection<PlayerSettingsSection> Sections { get; }

        public string FilePath => _config.MpvConfPath;

        public string LiveText => PlayerIpc.Endpoint != null
            ? "Changes are saved to mpv.conf and applied to the open player right away. A few take effect when the player restarts."
            : "Changes are saved to mpv.conf and take effect the next time the player starts. Open this page from the player to apply them right away.";

        public string Filter
        {
            get => _filter;
            set
            {
                this.RaiseAndSetIfChanged(ref _filter, value);
                foreach (var section in Sections) section.ApplyFilter(value);
            }
        }

        public string Status
        {
            get => _status;
            private set => this.RaiseAndSetIfChanged(ref _status, value);
        }

        public bool StatusIsError
        {
            get => _statusIsError;
            private set => this.RaiseAndSetIfChanged(ref _statusIsError, value);
        }

        public IEnumerable<PlayerOptionViewModel> Items => Sections.SelectMany(s => s.Items);

        public PlayerOptionViewModel? Find(string option) => Items.FirstOrDefault(i => i.Option.Name == option);

        public void ReloadFromDisk()
        {
            try
            {
                _config.Reload();
                RefreshAll();
                SetStatus("", false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus("Could not read mpv.conf: " + ex.Message, true);
            }
        }

        private void RefreshAll()
        {
            foreach (var item in Items)
                item.Refresh(_config.Effective(item.Option), _config.IsSet(item.Option), _config.BaseValue(item.Option));
            foreach (var section in Sections) section.RefreshSummary();
            this.RaisePropertyChanged(nameof(FilePath));
        }

        private void SetStatus(string text, bool error)
        {
            Status = text;
            StatusIsError = error;
            this.RaisePropertyChanged(nameof(StatusText));
        }

        // Always one line, so the settings below don't move when a message appears.
        public string StatusText => Status.Length > 0 ? Status : "Your settings file: " + FilePath;

        internal Task ApplyAsync(PlayerOptionViewModel item, string value)
        {
            _applying = ApplyAfter(_applying, item, value);
            return _applying;
        }

        public Task Idle => _applying;

        private async Task ApplyAfter(Task previous, PlayerOptionViewModel item, string value)
        {
            await previous;
            var option = item.Option;
            if (option.Validate(value) is { } invalid)
            {
                item.Error = invalid;
                return;
            }
            item.Error = null;
            try
            {
                // the file may have been edited by hand since it was read
                _config.Reload();
                if (_config.Set(option, value)) _config.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus("Could not save mpv.conf: " + ex.Message, true);
                // show what the file still says, not the change that wasn't saved
                try { _config.Reload(); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
                RefreshAll();
                return;
            }
            RefreshAll();
            string label = item.Label;
            if (PlayerIpc.Endpoint is not { } endpoint)
            {
                SetStatus($"Saved {label}.", false);
                return;
            }
            if (!option.Live)
            {
                SetStatus($"Saved {label}. It takes effect when the player restarts.", false);
                return;
            }
            string? error = await PlayerIpc.SetAsync(endpoint, option.Name, _config.Effective(option));
            if (error is null) SetStatus($"Saved {label} and applied it to the player.", false);
            else if (error == PlayerIpc.NotRunning)
            {
                // the player was closed: later changes only go to the file
                if (PlayerIpc.Endpoint == endpoint) PlayerIpc.Endpoint = null;
                SetStatus($"Saved {label}. It takes effect the next time the player starts.", false);
            }
            else SetStatus($"Saved {label}. The open player didn't take it ({error}); it applies when the player restarts.", true);
        }

        public void OpenFile()
        {
            try
            {
                if (!File.Exists(_config.MpvConfPath))
                {
                    SetStatus("mpv.conf doesn't exist yet. It's created when you change a setting.", false);
                    return;
                }
                Process.Start(new ProcessStartInfo(_config.MpvConfPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("Could not open mpv.conf: " + ex.Message, true);
            }
        }

        internal static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { /* no browser: nothing to do */ }
        }
    }

    public class PlayerSettingsSection : ReactiveObject
    {
        private bool _isVisible = true;
        private bool _filtering;
        private bool _filteredExpanded;
        private string _summary = "";

        public PlayerSettingsSection(string name, IEnumerable<PlayerOptionViewModel> items)
        {
            Name = name;
            Items = new(items);
        }

        public string Name { get; }
        public ObservableCollection<PlayerOptionViewModel> Items { get; }
        private string StateKey => "player:" + Name.ToLowerInvariant();

        public bool IsVisible
        {
            get => _isVisible;
            private set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        // Remembered like the Profiles tab's sections; while searching, sections with a match open
        // without changing what is remembered.
        public bool IsExpanded
        {
            get => _filtering ? _filteredExpanded : UiState.Current.GetExpanded(StateKey) ?? false;
            set
            {
                if (_filtering) _filteredExpanded = value;
                else UiState.Current.SetExpanded(StateKey, value);
                this.RaisePropertyChanged();
            }
        }

        public string Summary
        {
            get => _summary;
            private set => this.RaiseAndSetIfChanged(ref _summary, value);
        }

        public void RefreshSummary()
        {
            int changed = Items.Count(i => i.IsSet);
            Summary = changed switch
            {
                0 => string.Join(", ", Items.Take(3).Select(i => i.Label.ToLowerInvariant())) + (Items.Count > 3 ? ", ..." : ""),
                1 => "1 setting changed",
                _ => $"{changed} settings changed",
            };
        }

        public void ApplyFilter(string filter)
        {
            filter = filter.Trim();
            bool any = false;
            foreach (var item in Items)
            {
                item.IsVisible = filter.Length == 0 || item.Matches(filter);
                any |= item.IsVisible;
            }
            IsVisible = any;
            _filtering = filter.Length > 0;
            _filteredExpanded = any;
            this.RaisePropertyChanged(nameof(IsExpanded));
        }
    }

    public class PlayerOptionViewModel : ReactiveObject
    {
        private readonly PlayerSettingsViewModel _owner;
        private bool _refreshing;
        private bool _flag;
        private OptionChoice? _selectedChoice;
        private string _text = "";
        private bool _isSet;
        private string _baseValue = "";
        private string? _error;
        private bool _isVisible = true;
        private List<OptionChoice> _choices;

        public PlayerOptionViewModel(PlayerSettingsViewModel owner, PlayerOption option)
        {
            _owner = owner;
            Option = option;
            _choices = option.Choices.ToList();
        }

        public PlayerOption Option { get; }
        public string Label => Option.Label;
        public string Help => Option.Help + (Option.Live ? "" : " Takes effect when the player starts.");
        public string OptionName => Option.Name;
        public string? Placeholder => Option.Placeholder;
        public string ManualTip => "Open mpv's manual for " + Option.Name;

        public bool IsFlag => Option.Type == OptionType.Flag;
        public bool IsChoice => Option.Type == OptionType.Choice && !Option.Editable;
        public bool IsEditableChoice => Option.Type == OptionType.Choice && Option.Editable;
        public bool IsText => Option.Type is OptionType.Text or OptionType.Number or OptionType.Bytes or OptionType.Folder;
        public bool IsFolder => Option.Type == OptionType.Folder;

        public void Reset() => _ = _owner.ApplyAsync(this, _baseValue);
        public void OpenManual() => PlayerSettingsViewModel.OpenUrl(Option.ManualUrl);

        public List<OptionChoice> Choices
        {
            get => _choices;
            private set => this.RaiseAndSetIfChanged(ref _choices, value);
        }

        public bool FlagValue
        {
            get => _flag;
            set
            {
                if (_flag == value) return;
                this.RaiseAndSetIfChanged(ref _flag, value);
                if (!_refreshing) _ = _owner.ApplyAsync(this, value ? "yes" : "no");
            }
        }

        public OptionChoice? SelectedChoice
        {
            get => _selectedChoice;
            set
            {
                if (value is null || value == _selectedChoice) return;
                this.RaiseAndSetIfChanged(ref _selectedChoice, value);
                if (!_refreshing) _ = _owner.ApplyAsync(this, value.Value);
            }
        }

        // Text boxes, committed when they lose focus or on Enter; an editable list is a text box
        // with a menu of common values.
        public string Text
        {
            get => _text;
            set
            {
                value ??= "";
                if (_text == value) return;
                this.RaiseAndSetIfChanged(ref _text, value);
                if (_refreshing) return;
                // an editable list shows known values by their label
                string raw = IsEditableChoice ? Choices.FirstOrDefault(c => c.Label == value.Trim())?.Value ?? value : value;
                _ = _owner.ApplyAsync(this, raw);
            }
        }

        // An entry picked from an editable list's menu.
        public void Pick(OptionChoice choice) => Text = choice.Label;

        public string Tip => Help + Environment.NewLine + Environment.NewLine + "mpv option: " + Option.Name +
            (Option.Aliases.Count > 0 ? " (also " + string.Join(", ", Option.Aliases) + ")" : "");

        public bool IsSet
        {
            get => _isSet;
            private set => this.RaiseAndSetIfChanged(ref _isSet, value);
        }

        public string DefaultText => "Default: " + Display(_baseValue);

        public string? Error
        {
            get => _error;
            set
            {
                this.RaiseAndSetIfChanged(ref _error, value);
                this.RaisePropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => _error != null;

        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        public bool Matches(string filter) =>
            Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Option.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Option.Aliases.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
            Option.Help.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private string Display(string value)
        {
            if (IsFlag) return PlayerOption.FlagValue(value) == "yes" ? "on" : "off";
            if (Choices.FirstOrDefault(c => Option.SameValue(c.Value, value)) is { } choice) return choice.Label;
            return value.Length == 0 ? "(none)" : value;
        }

        internal void Refresh(string effective, bool isSet, string baseValue)
        {
            _refreshing = true;
            try
            {
                Error = null;
                _baseValue = baseValue;
                IsSet = isSet;
                this.RaisePropertyChanged(nameof(DefaultText));
                switch (Option.Type)
                {
                    case OptionType.Flag:
                        FlagValue = PlayerOption.FlagValue(effective) == "yes";
                        break;
                    case OptionType.Choice:
                        var choice = Choices.FirstOrDefault(c => Option.SameValue(c.Value, effective));
                        if (choice is null && !Option.Editable)
                        {
                            // a value from the file the list doesn't offer: show it as it is
                            choice = new OptionChoice(effective, effective.Length == 0 ? "(none)" : effective);
                            Choices = Option.Choices.Append(choice).ToList();
                        }
                        if (Option.Editable) Text = choice?.Label ?? effective;
                        else { _selectedChoice = choice; this.RaisePropertyChanged(nameof(SelectedChoice)); }
                        break;
                    default:
                        Text = effective;
                        break;
                }
            }
            finally { _refreshing = false; }
        }
    }
}
