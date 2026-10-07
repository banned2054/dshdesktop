using DshDesktop.Core.Models;
using DshDesktop.Services.Conversations;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;

namespace DshDesktop.ViewModels;

/// <summary>One visible edited-files card composed from the independent change and deliverable sources.</summary>
public sealed class EditedFilesCardViewModel : ConversationItemViewModel
{
    private const int CollapsedFileCount = 3;
    private readonly string? _cwd;
    private WorkspaceChangesCardViewModel? _changes;
    private DeliverablesCardViewModel? _deliverables;
    private bool _isExpanded;

    public EditedFilesCardViewModel(long turn, string? cwd) : base(0)
    {
        Turn = turn;
        _cwd = cwd;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public long Turn { get; }

    public ObservableCollection<EditedFileRowViewModel> Files { get; } = [];

    public ICommand ToggleCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(VisibleFiles));
                OnPropertyChanged(nameof(ToggleText));
            }
        }
    }

    public bool HasToggle => Files.Count > CollapsedFileCount;

    public IReadOnlyList<EditedFileRowViewModel> VisibleFiles => IsExpanded || !HasToggle
        ? Files.ToArray()
        : Files.Take(CollapsedFileCount).ToArray();

    public string ToggleText => IsExpanded ? "收起文件" : $"再显示 {Files.Count - CollapsedFileCount} 个文件";

    public bool IsSingleFile => Files.Count == 1;

    public EditedFileRowViewModel? SingleFile => Files.Count == 1 ? Files[0] : null;

    public string SingleTitleText => SingleFile is { } file ? $"已编辑 {file.Name}" : TitleText;

    public string SingleActionText => SingleFile?.HasChange == true ? "查看变更" : "查看文件";

    public bool SingleHasLineCounts => SingleFile?.HasLineCounts == true;

    public bool SingleHasCountText => SingleFile?.HasCountText == true;

    public string SingleStatusText => SingleFile?.StatusText ?? string.Empty;

    public bool SingleHasError => SingleFile?.HasError == true;

    public string TitleText => $"已编辑 {Files.Count} 个文件";

    public bool HasTotals => _deliverables is null && _changes?.HasTotals == true;

    public bool HasFiles => Files.Count > 0;

    public string HeaderAddedText => _deliverables is null ? _changes?.HeaderAddedText ?? string.Empty : string.Empty;

    public string HeaderDeletedText => _deliverables is null ? _changes?.HeaderDeletedText ?? string.Empty : string.Empty;

    public bool HasError => _changes?.HasError == true;

    public bool IsUnavailable => _changes?.IsUnavailable == true;

    public string SummaryText => _changes?.SummaryText ?? string.Empty;

    public void SetChanges(WorkspaceChangesCardViewModel? changes)
    {
        if (_changes is not null) _changes.PropertyChanged -= OnChangesPropertyChanged;
        if (_changes is not null) _changes.Files.CollectionChanged -= OnSourceFilesChanged;
        _changes = changes;
        if (_changes is not null)
        {
            _changes.PropertyChanged += OnChangesPropertyChanged;
            _changes.Files.CollectionChanged += OnSourceFilesChanged;
        }

        RebuildRows();
        NotifyAll();
    }

    public void SetDeliverables(DeliverablesCardViewModel? deliverables)
    {
        if (_deliverables is not null) _deliverables.PropertyChanged -= OnDeliverablesPropertyChanged;
        if (_deliverables is not null) _deliverables.Files.CollectionChanged -= OnSourceFilesChanged;
        _deliverables = deliverables;
        if (_deliverables is not null)
        {
            _deliverables.PropertyChanged += OnDeliverablesPropertyChanged;
            _deliverables.Files.CollectionChanged += OnSourceFilesChanged;
        }

        RebuildRows();
        NotifyAll();
    }

    private void OnSourceFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildRows();
        NotifyAll();
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditedFileRowViewModel.HasLineCounts) or
            nameof(EditedFileRowViewModel.HasCountText) or
            nameof(EditedFileRowViewModel.StatusText) or
            nameof(EditedFileRowViewModel.HasError))
            NotifyAll();
    }

    private void OnChangesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceChangesCardViewModel.HasTotals) or
            nameof(WorkspaceChangesCardViewModel.HeaderAddedText) or
            nameof(WorkspaceChangesCardViewModel.HeaderDeletedText) or
            nameof(WorkspaceChangesCardViewModel.HasError) or
            nameof(WorkspaceChangesCardViewModel.IsUnavailable) or
            nameof(WorkspaceChangesCardViewModel.SummaryText))
            NotifyAll();
    }

    private void OnDeliverablesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeliverablesCardViewModel.HasTotals) or
            nameof(DeliverablesCardViewModel.HeaderAddedText) or
            nameof(DeliverablesCardViewModel.HeaderDeletedText))
            NotifyAll();
    }

    private void RebuildRows()
    {
        var rows = new List<EditedFileRowViewModel>();
        var matched = new HashSet<DeliveredFileViewModel>();
        if (_changes is not null)
        {
            foreach (var change in _changes.Files)
            {
                var delivery = _deliverables?.Files.FirstOrDefault(file => SamePath(change.Source.Path, file.Path));
                if (delivery is not null) matched.Add(delivery);
                var row = new EditedFileRowViewModel(change, delivery);
                row.SetChangeOwner(_changes);
                rows.Add(row);
            }
        }

        if (_deliverables is not null)
            rows.AddRange(_deliverables.Files.Where(file => !matched.Contains(file))
                .Select(file => new EditedFileRowViewModel(null, file)));

        foreach (var previous in Files) previous.PropertyChanged -= OnRowPropertyChanged;
        Files.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            Files.Add(row);
        }
    }

    private bool SamePath(string changePath, string deliveredPath)
    {
        var change = WorkspacePathResolver.TryResolve(changePath, _cwd, out _);
        var delivered = WorkspacePathResolver.TryResolve(deliveredPath, _cwd, out _);
        return change is not null && delivered is not null && WorkspacePathResolver.SamePath(change, delivered);
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(VisibleFiles));
        OnPropertyChanged(nameof(HasToggle));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(IsSingleFile));
        OnPropertyChanged(nameof(SingleFile));
        OnPropertyChanged(nameof(SingleTitleText));
        OnPropertyChanged(nameof(SingleActionText));
        OnPropertyChanged(nameof(SingleHasLineCounts));
        OnPropertyChanged(nameof(SingleHasCountText));
        OnPropertyChanged(nameof(SingleStatusText));
        OnPropertyChanged(nameof(SingleHasError));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(HasTotals));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HeaderAddedText));
        OnPropertyChanged(nameof(HeaderDeletedText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(SummaryText));
    }
}

public sealed class EditedFileRowViewModel : ObservableObject
{
    private readonly WorkspaceChangedFileViewModel? _change;
    private readonly DeliveredFileViewModel? _deliverable;

    internal EditedFileRowViewModel(WorkspaceChangedFileViewModel? change, DeliveredFileViewModel? deliverable)
    {
        _change = change;
        _deliverable = deliverable;
        if (change is not null) change.PropertyChanged += (_, _) => Refresh();
        if (deliverable is not null) deliverable.PropertyChanged += (_, _) => Refresh();
        OpenCommand = new RelayCommand(() =>
        {
            if (_change is not null) _changeOwner?.SelectFileCommand.Execute(_change);
            else _deliverable?.OpenCommand.Execute(null);
        });
        ChangeCommand = OpenCommand;
        DeliveryCommand = new RelayCommand(() => _deliverable?.OpenCommand.Execute(null));
    }

    private WorkspaceChangesCardViewModel? _changeOwner;

    internal void SetChangeOwner(WorkspaceChangesCardViewModel owner) => _changeOwner = owner;

    public string Path => _change?.Display ?? _deliverable?.Path ?? string.Empty;

    public string Name => _change is not null
        ? WorkspaceDiffPanelViewModel.DisplayName(_change.Display)
        : _deliverable?.Name ?? string.Empty;

    public string AddedText => _deliverable is null ? _change?.AddedText ?? string.Empty : string.Empty;

    public string DeletedText => _deliverable is null ? _change?.DeletedText ?? string.Empty : string.Empty;

    public bool HasLineCounts => _deliverable is null && _change?.HasLineCounts == true;

    public bool HasCountText => _deliverable is null && _change is not null && !_change.HasLineCounts;

    public string CountText => _deliverable is null ? _change?.CountText ?? string.Empty : string.Empty;

    public string StatusText => _deliverable?.StatusText ?? string.Empty;

    public bool HasError => _deliverable?.HasError == true;

    public string? ToolTip => _deliverable?.Path ?? _change?.Display;

    public bool HasChange => _change is not null;

    public bool HasDeliverable => _deliverable is not null;

    public ICommand OpenCommand { get; }

    public ICommand ChangeCommand { get; }

    public ICommand DeliveryCommand { get; }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(DeletedText));
        OnPropertyChanged(nameof(HasLineCounts));
        OnPropertyChanged(nameof(HasCountText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasError));
    }
}
