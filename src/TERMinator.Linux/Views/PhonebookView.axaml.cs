using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TERMinator.Linux.Models;
using TERMinator.Linux.ViewModels;

namespace TERMinator.Linux.Views;

public partial class PhonebookView : UserControl
{
    private Point _dragStartPoint;
    private SavedConnection? _draggedItem;
    private ListBoxItem? _draggedContainer;

    public PhonebookView()
    {
        InitializeComponent();
        AttachedToVisualTree += PhonebookView_AttachedToVisualTree;
    }

    private void PhonebookView_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is PhonebookViewModel vm)
        {
            vm.PropertyChanged += ViewModel_PropertyChanged;
            ApplyViewMode(vm.ViewMode);
        }

        // Wire up double-click and drag-drop on the ListBox
        TileList.DoubleTapped += TileList_DoubleTapped;
        TileList.PointerPressed += TileList_PointerPressed;
        TileList.PointerMoved += TileList_PointerMoved;

        TileList.AddHandler(DragDrop.DragOverEvent, TileList_DragOver);
        TileList.AddHandler(DragDrop.DropEvent, TileList_Drop);
        DragDrop.SetAllowDrop(TileList, true);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhonebookViewModel.ViewMode) && sender is PhonebookViewModel vm)
            ApplyViewMode(vm.ViewMode);
    }

    private void ApplyViewMode(int mode)
    {
        var templateKey = mode switch
        {
            0 => "ListTemplate",
            2 => "LargeTileTemplate",
            3 => "SmallTileTemplate",
            _ => "MediumTileTemplate"
        };

        if (Resources.TryGetResource(templateKey, null, out var templateObj) && templateObj is IDataTemplate template)
        {
            TileList.ItemTemplate = template;
        }

        // Avalonia ListBox uses ItemsPanel for layout.
        // For tile modes use WrapPanel, for list mode use StackPanel.
        if (mode == 0)
        {
            TileList.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
        }
        else
        {
            TileList.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });
        }

        // Windows swaps the item container style per mode; here one style covers both and the class picks the
        // spacing: 6 px between tiles, 2 px between list rows.
        TileList.Classes.Set("list-mode", mode == 0);
    }

    private void TileList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PhonebookViewModel vm && vm.SelectedConnection != null)
        {
            vm.EditConnectionCommand.Execute(null);
        }
    }

    private void TileList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (point.Properties.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(null);
            (_draggedItem, _draggedContainer) = GetItemAtPointerEvent(e);
        }
    }

    private async void TileList_PointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed || _draggedItem == null)
            return;

        var pos = e.GetPosition(null);
        var diff = _dragStartPoint - pos;

        if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
        {
            // Fade the source item during drag (matching Windows DragAdorner behavior)
            if (_draggedContainer != null)
                _draggedContainer.Opacity = 0.3;

            var data = new DataObject();
            data.Set("SavedConnection", _draggedItem);
            try
            {
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
            finally
            {
                if (_draggedContainer != null)
                    _draggedContainer.Opacity = 1.0;
                _draggedItem = null;
                _draggedContainer = null;
            }
        }
    }

    private void TileList_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains("SavedConnection"))
            e.DragEffects = DragDropEffects.Move;
        else
            e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private void TileList_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PhonebookViewModel vm) return;
        if (!e.Data.Contains("SavedConnection")) return;

        var dragged = e.Data.Get("SavedConnection") as SavedConnection;
        if (dragged == null) return;

        var target = GetItemAtDragPoint(e);
        if (target == null || target.Id == dragged.Id) return;

        int targetIndex = -1;
        for (int i = 0; i < vm.Connections.Count; i++)
        {
            if (vm.Connections[i].Id == target.Id)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex >= 0)
            vm.MoveConnectionTo(dragged.Id, targetIndex);
    }

    private (SavedConnection?, ListBoxItem?) GetItemAtPointerEvent(PointerPressedEventArgs e)
    {
        var source = e.Source as Visual;
        while (source != null)
        {
            if (source is ListBoxItem item)
                return (item.DataContext as SavedConnection, item);
            source = source.GetVisualParent() as Visual;
        }
        return (null, null);
    }

    private SavedConnection? GetItemAtDragPoint(DragEventArgs e)
    {
        var pos = e.GetPosition(TileList);
        // Walk up from the InputElement at the drag position
        var source = e.Source as Visual;
        while (source != null)
        {
            if (source is ListBoxItem item)
                return item.DataContext as SavedConnection;
            source = source.GetVisualParent() as Visual;
        }
        return null;
    }
}
