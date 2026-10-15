using TWS2CESteamModManager.App.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TWS2CESteamModManager.App.ViewModels;

namespace TWS2CESteamModManager.App.Controls;

public partial class ModsView : UserControl
{
    private const string DragFormat = "TWS2CESteamModManager.ModReorder";
    private readonly Guid dragOwner = Guid.NewGuid();
    private Guid? dragCandidate;
    private Point dragStart;
    private UIElement? capturedHandle;
    private bool dragging;
    private long lastScroll;
    private double headerHeight = 40;
    private ScrollViewer? gridScroll;
    private ModDropAdorner? dropIndicator;
    private AdornerLayer? dropLayer;

    public ModsView()
    {
        InitializeComponent();
        Unloaded += (_, _) => { ResetDragCandidate(); ClearDropIndicator(); };
    }

    private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ModManagerViewModel { CanManage: true } mods
            || sender is not FrameworkElement { DataContext: InstalledModViewModel row }) return;
        dragCandidate = row.Id;
        dragStart = e.GetPosition(ModsGrid);
        capturedHandle = (UIElement)sender;
        capturedHandle.CaptureMouse();
        mods.SelectedMod = row;
        gridScroll = FindChild<ScrollViewer>(ModsGrid);
        headerHeight = FindChild<DataGridColumnHeadersPresenter>(ModsGrid)?.ActualHeight ?? 40;
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { ResetDragCandidate(); return; }
        if (dragging || dragCandidate is not { } id || DataContext is not ModManagerViewModel { CanManage: true }) return;
        Point position = e.GetPosition(ModsGrid);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var data = new DataObject();
        data.SetData(DragFormat, $"{dragOwner:N}:{id:N}", autoConvert: false);
        capturedHandle?.ReleaseMouseCapture();
        capturedHandle = null;
        dragging = true;
        e.Handled = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move); }
        finally { dragging = false; ResetDragCandidate(); ClearDropIndicator(); }
    }

    private void DragHandle_MouseUp(object sender, MouseButtonEventArgs e) { ResetDragCandidate(); e.Handled = true; }
    private void ResetDragCandidate()
    {
        dragCandidate = null;
        capturedHandle?.ReleaseMouseCapture();
        capturedHandle = null;
    }

    private void DragHandle_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (DataContext is not ModManagerViewModel { CanManage: true }) { e.Action = DragAction.Cancel; e.Handled = true; }
    }

    private bool TryGetDraggedMod(IDataObject data, out Guid id)
    {
        id = default;
        if (DataContext is not ModManagerViewModel { CanManage: true } mods
            || !data.GetDataPresent(DragFormat, autoConvert: false)
            || data.GetData(DragFormat, autoConvert: false) is not string value) return false;
        string[] parts = value.Split(':');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out Guid owner) || owner != dragOwner
            || !Guid.TryParseExact(parts[1], "N", out Guid candidate)) return false;
        id = candidate;
        return mods.Items.Any(mod => mod.Id == candidate);
    }

    private (int Boundary, double Y)? InsertionPoint(DependencyObject? source, Point position)
    {
        if (DataContext is not ModManagerViewModel mods || mods.Items.Count == 0
            || position.Y < headerHeight || position.Y > ModsGrid.ActualHeight
            || position.X < 0 || position.X > ModsGrid.ActualWidth - SystemParameters.VerticalScrollBarWidth) return null;
        if (source is not null && ItemsControl.ContainerFromElement(ModsGrid, source) is DataGridRow { Item: InstalledModViewModel item } row)
        {
            double top = row.TranslatePoint(new Point(), ModsGrid).Y;
            bool after = position.Y >= top + row.ActualHeight / 2;
            return (item.Order - 1 + (after ? 1 : 0), after ? top + row.ActualHeight : top);
        }
        DataGridRow? lastRow = ModsGrid.ItemContainerGenerator.ContainerFromIndex(mods.Items.Count - 1) as DataGridRow;
        if (lastRow is null) return null;
        double bottom = lastRow.TranslatePoint(new Point(), ModsGrid).Y + lastRow.ActualHeight;
        return position.Y >= bottom ? (mods.Items.Count, bottom) : null;
    }

    private void ModsGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!TryGetDraggedMod(e.Data, out _)) { ClearDropIndicator(); return; }
        Point position = e.GetPosition(ModsGrid);
        if (Environment.TickCount64 - lastScroll >= 100)
        {
            if (position.Y < headerHeight + 28) gridScroll?.LineUp();
            else if (position.Y > ModsGrid.ActualHeight - 28) gridScroll?.LineDown();
            lastScroll = Environment.TickCount64;
        }
        if (InsertionPoint(e.OriginalSource as DependencyObject, position) is not { } insertion) { ClearDropIndicator(); return; }
        e.Effects = DragDropEffects.Move;
        dropLayer ??= AdornerLayer.GetAdornerLayer(ModsGrid);
        if (dropLayer is null) return;
        if (dropIndicator is null)
        {
            dropIndicator = new ModDropAdorner(ModsGrid) { IsHitTestVisible = false };
            dropLayer.Add(dropIndicator);
        }
        dropIndicator.PositionY = insertion.Y;
        dropIndicator.InvalidateVisual();
    }

    private async void ModsGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ClearDropIndicator();
        if (!TryGetDraggedMod(e.Data, out Guid id) || DataContext is not ModManagerViewModel mods
            || InsertionPoint(e.OriginalSource as DependencyObject, e.GetPosition(ModsGrid)) is not { } insertion) return;
        int sourceIndex = mods.Items.Single(mod => mod.Id == id).Order - 1;
        int targetIndex = ModReorder.TargetIndex(sourceIndex, insertion.Boundary, mods.Items.Count);
        e.Effects = DragDropEffects.Move;
        await mods.MoveToIndexAsync(id, targetIndex);
    }

    private void ModsGrid_DragLeave(object sender, DragEventArgs e)
    {
        Point position = e.GetPosition(ModsGrid);
        if (position.X < 0 || position.Y < 0 || position.X > ModsGrid.ActualWidth || position.Y > ModsGrid.ActualHeight) ClearDropIndicator();
    }

    private void ClearDropIndicator()
    {
        if (dropIndicator is not null) dropLayer?.Remove(dropIndicator);
        dropIndicator = null;
        dropLayer = null;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ModManagerViewModel { CanManage: true } mods) return;
        var picker = new OpenFileDialog { Title = L.Text("dialog.importTitle"), Filter = L.Text("dialog.packFilter") + "|*.pack", Multiselect = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await mods.ImportAsync(picker.FileNames);
    }

    private async void ModName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledModViewModel mod } && Window.GetWindow(this) is MainWindow window)
            await window.Shell.OpenInstalledDetailsAsync(mod);
    }

    private void SelectAppData_Click(object sender, RoutedEventArgs e) => SelectFolder(true);
    private void SelectGame_Click(object sender, RoutedEventArgs e) => SelectFolder(false);
    private void SelectFolder(bool appData)
    {
        if (DataContext is not ModManagerViewModel { CanConfigure: true } mods) return;
        var picker = new OpenFolderDialog { Title = appData ? L.Text("dialog.selectAppData") : L.Text("dialog.selectGame") };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        if (appData) mods.AppDataPath = picker.FolderName;
        else mods.GameInstallPath = picker.FolderName;
    }
}
