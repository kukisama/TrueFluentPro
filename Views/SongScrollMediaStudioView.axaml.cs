using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

/// <summary>Independent long-scroll presentation; the inherited view supplies business interactions only.</summary>
public partial class SongScrollMediaStudioView : MediaStudioView, ISongScrollPage
{
    private readonly ObservableCollection<MediaSessionViewModel> _directory = new();
    private readonly HashSet<MediaSessionViewModel> _observedSessions = new();
    private MediaStudioViewModel? _directoryVm;
    private bool _runningOnly;
    private bool _refreshingDirectory;

    public SongScrollShell Shell => this.FindControl<SongScrollShell>("BookShell")!;
    protected override bool UseMessageHeightCache => false;

    public SongScrollMediaStudioView() : base(false)
    {
        InitializeComponent();
    }

    public override void BindViewModel(MediaStudioViewModel vm)
    {
        base.BindViewModel(vm);
        if (ReferenceEquals(_directoryVm, vm)) return;
        _directoryVm = vm;
        vm.Sessions.CollectionChanged += Sessions_Changed;
        vm.PropertyChanged += DirectoryVm_Changed;
        this.FindControl<ListBox>("SessionListBox")!.ItemsSource = _directory;
        RefreshDirectory();
    }

    public override void UnbindViewModel()
    {
        if (_directoryVm != null)
        {
            _directoryVm.Sessions.CollectionChanged -= Sessions_Changed;
            _directoryVm.PropertyChanged -= DirectoryVm_Changed;
        }
        foreach (var session in _observedSessions) session.PropertyChanged -= Session_Changed;
        _observedSessions.Clear();
        _directoryVm = null;
        _directory.Clear();
        base.UnbindViewModel();
    }

    private void Sessions_Changed(object? sender, NotifyCollectionChangedEventArgs e) => RefreshDirectory();

    private void DirectoryVm_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaStudioViewModel.CurrentSession)) RefreshDirectory();
    }

    private void Session_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaSessionViewModel.SessionName) or nameof(MediaSessionViewModel.HasBadge)
            or nameof(MediaSessionViewModel.IsChatStreaming))
            RefreshDirectory();
    }

    private void DirectoryFilter_Changed(object? sender, TextChangedEventArgs e) => RefreshDirectory();

    private void AllSessions_Click(object? sender, RoutedEventArgs e)
    {
        _runningOnly = false;
        RefreshDirectory();
    }

    private void RunningSessions_Click(object? sender, RoutedEventArgs e)
    {
        _runningOnly = true;
        RefreshDirectory();
    }

    private void RefreshDirectory()
    {
        if (_directoryVm == null || _refreshingDirectory) return;
        _refreshingDirectory = true;
        try
        {
            var sessions = _directoryVm.Sessions;
            foreach (var old in _observedSessions.Where(s => !sessions.Contains(s)).ToList())
            {
                old.PropertyChanged -= Session_Changed;
                _observedSessions.Remove(old);
            }
            foreach (var session in sessions)
                if (_observedSessions.Add(session)) session.PropertyChanged += Session_Changed;

            var query = this.FindControl<TextBox>("SessionFilterTextBox")?.Text?.Trim() ?? "";
            var matches = sessions.Where(s => (!_runningOnly || s.HasBadge || s.IsChatStreaming)
                && (query.Length == 0 || s.SessionName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            // Preserve VM selection when filtering removes an item from the presentation list.
            for (var i = _directory.Count - 1; i >= 0; i--)
                if (!matches.Contains(_directory[i])) _directory.RemoveAt(i);
            for (var i = 0; i < matches.Count; i++)
            {
                var existing = _directory.IndexOf(matches[i]);
                if (existing < 0) _directory.Insert(i, matches[i]);
                else if (existing != i) _directory.Move(existing, i);
            }
            this.FindControl<ListBox>("SessionListBox")!.SelectedItem =
                matches.Contains(_directoryVm.CurrentSession!) ? _directoryVm.CurrentSession : null;
            this.FindControl<Button>("AllSessionsButton")!.Classes.Set("selected", !_runningOnly);
            this.FindControl<Button>("RunningSessionsButton")!.Classes.Set("selected", _runningOnly);
        }
        finally { _refreshingDirectory = false; }
    }

    private void DirectorySelection_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingDirectory && _directoryVm != null && e.AddedItems.Count > 0
            && e.AddedItems[0] is MediaSessionViewModel session)
            _directoryVm.CurrentSession = session;
    }
}