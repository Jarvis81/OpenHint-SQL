using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Editor;
using OpenHintSQL.Providers;
using OpenHintSQL.Utils;

namespace OpenHintSQL.UI
{
    /// <summary>
    /// Custom WPF popup for displaying SQL completion items.
    /// Dark-themed to match SSMS, with virtualized rendering for performance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The popup does NOT capture keyboard focus — all keyboard events are routed through
    /// <see cref="Completion.CompletionCommandFilter"/> which calls <see cref="SelectNext"/>,
    /// <see cref="SelectPrevious"/>, and <see cref="AcceptSelected"/> as needed.
    /// </para>
    /// <para>
    /// All public methods are thread-safe: they dispatch to the UI thread when necessary.
    /// </para>
    /// </remarks>
    internal sealed class CompletionPopup
    {
        // ═══════════════════════════════════════════════════════════════
        //  THEME COLORS
        // ═══════════════════════════════════════════════════════════════

        private static readonly Color BackgroundColor = (Color)ColorConverter.ConvertFromString("#FFFFFF");
        private static readonly Color ForegroundColor = (Color)ColorConverter.ConvertFromString("#1F2328");
        private static readonly Color SelectionColor = (Color)ColorConverter.ConvertFromString("#DDEBFF");
        private static readonly Color HoverColor = (Color)ColorConverter.ConvertFromString("#F6F8FA");
        private static readonly Color BorderColor = (Color)ColorConverter.ConvertFromString("#D0D7DE");
        private static readonly Color DescriptionColor = (Color)ColorConverter.ConvertFromString("#6E7781");
        private static readonly Color IconBackgroundColor = (Color)ColorConverter.ConvertFromString("#EEF4FF");
        private static readonly Color IconForegroundColor = (Color)ColorConverter.ConvertFromString("#0969DA");

        private static readonly Brush BackgroundBrush = new SolidColorBrush(BackgroundColor);
        private static readonly Brush ForegroundBrush = new SolidColorBrush(ForegroundColor);
        private static readonly Brush SelectionBrush = new SolidColorBrush(SelectionColor);
        private static readonly Brush HoverBrush = new SolidColorBrush(HoverColor);
        private static readonly Brush BorderBrush_ = new SolidColorBrush(BorderColor);
        private static readonly Brush DescriptionBrush = new SolidColorBrush(DescriptionColor);
        private static readonly Brush IconBackgroundBrush = new SolidColorBrush(IconBackgroundColor);
        private static readonly Brush IconForegroundBrush = new SolidColorBrush(IconForegroundColor);
        private static readonly Brush TransparentBrush = Brushes.Transparent;

        // ═══════════════════════════════════════════════════════════════
        //  LAYOUT CONSTANTS
        // ═══════════════════════════════════════════════════════════════

        private const double PopupMaxHeight = 300;
        private const double PopupWidth = 440;
        private const double FadeInDurationMs = 120;

        // ═══════════════════════════════════════════════════════════════
        //  UI ELEMENTS
        // ═══════════════════════════════════════════════════════════════

        private readonly Popup _popup;
        private readonly ListBox _listBox;
        private readonly Border _border;

        // ═══════════════════════════════════════════════════════════════
        //  STATE
        // ═══════════════════════════════════════════════════════════════

        private List<CompletionItemData> _allItems;
        private List<CompletionItemData> _filteredItems;
        private string _currentPrefix;
        private IWpfTextView _currentView;

        /// <summary>
        /// Fired when a completion item is accepted (via Enter, Tab, or mouse click).
        /// </summary>
        public event Action<CompletionItemData> ItemAccepted;

        /// <summary>
        /// Gets whether the popup is currently visible.
        /// </summary>
        public bool IsVisible
        {
            get
            {
                try { return _popup.IsOpen; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Gets whether the popup currently has any items to display.
        /// </summary>
        public bool HasItems
        {
            get
            {
                try { return _filteredItems != null && _filteredItems.Count > 0; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Gets whether the popup is currently displaying a non-insertable status row
        /// such as schema-loading feedback.
        /// </summary>
        public bool IsShowingStatus
        {
            get
            {
                try
                {
                    return _filteredItems != null &&
                           _filteredItems.Any(item => item.Kind == CompletionItemKind.Status);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Gets the currently selected item, or null if nothing is selected.
        /// </summary>
        public CompletionItemData SelectedItem
        {
            get
            {
                try
                {
                    return _listBox.SelectedItem as CompletionItemData;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Creates a new completion popup with dark-themed styling.
        /// Must be called on the UI thread.
        /// </summary>
        public CompletionPopup()
        {
            // Freeze shared brushes for cross-thread safety
            FreezeBrushes();

            // Build the ListBox with virtualization
            _listBox = CreateListBox();

            // Wrap in a border for styling
            _border = new Border
            {
                Background = BackgroundBrush,
                BorderBrush = BorderBrush_,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = _listBox,
                MaxHeight = PopupMaxHeight,
                Width = PopupWidth,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 12,
                    ShadowDepth = 2,
                    Opacity = 0.18
                }
            };

            // Create the popup container
            _popup = new Popup
            {
                Child = _border,
                AllowsTransparency = true,
                Placement = PlacementMode.RelativePoint,
                StaysOpen = false,
                Focusable = false,
                PopupAnimation = PopupAnimation.None // We handle animation manually
            };

            // Prevent the popup from stealing focus
            _popup.Opened += (s, e) =>
            {
                // The popup must not take focus away from the editor
                try
                {
                    var hwndSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(_popup.Child);
                    if (hwndSource != null)
                    {
                        // WS_EX_NOACTIVATE prevents focus stealing
                        const int WS_EX_NOACTIVATE = 0x08000000;
                        const int GWL_EXSTYLE = -20;
                        
                        IntPtr exStyle = NativeMethods.GetWindowLongPtr(hwndSource.Handle, GWL_EXSTYLE);
                        IntPtr newStyle = new IntPtr(exStyle.ToInt64() | WS_EX_NOACTIVATE);
                        NativeMethods.SetWindowLongPtr(hwndSource.Handle, GWL_EXSTYLE, newStyle);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not set WS_EX_NOACTIVATE: {ex.Message}");
                }
            };

            _allItems = new List<CompletionItemData>();
            _filteredItems = new List<CompletionItemData>();
        }

        // ═══════════════════════════════════════════════════════════════
        //  PUBLIC METHODS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows the completion popup at the caret position with the given items.
        /// </summary>
        /// <param name="view">The WPF text view (used for dismissal tracking).</param>
        /// <param name="items">The completion items to display.</param>
        public void Show(IWpfTextView view, List<CompletionItemData> items)
        {
            EnsureUIThread(() =>
            {
                try
                {
                    _currentView = view;
                    _allItems = items ?? new List<CompletionItemData>();
                    _filteredItems = new List<CompletionItemData>(_allItems);
                    _currentPrefix = string.Empty;

                    _listBox.ItemsSource = _filteredItems;
                    Logger.Log($"CompletionPopup.Show with {_filteredItems.Count} item(s)");

                    if (_filteredItems.Count > 0)
                    {
                        _listBox.SelectedIndex = 0;
                    }

                    ResetHorizontalScroll();
                    PositionAtCaret();

                    // Show with fade-in animation
                    _border.Opacity = 0;
                    _popup.IsOpen = true;
                    QueueResetHorizontalScroll();

                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInDurationMs))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    _border.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                catch (Exception ex)
                {
                    Logger.Error("CompletionPopup.Show failed", ex);
                }
            });
        }

        /// <summary>
        /// Dismisses (hides) the completion popup.
        /// </summary>
        public void Dismiss()
        {
            EnsureUIThread(() =>
            {
                try
                {
                    _popup.IsOpen = false;
                    _currentView = null;
                    _allItems = new List<CompletionItemData>();
                    _filteredItems = new List<CompletionItemData>();
                    _listBox.ItemsSource = null;
                }
                catch (Exception ex)
                {
                    Logger.Error("CompletionPopup.Dismiss failed", ex);
                }
            });
        }

        /// <summary>
        /// Moves the selection to the next item in the list.
        /// </summary>
        public void SelectNext()
        {
            EnsureUIThread(() =>
            {
                try
                {
                    if (_listBox.Items.Count == 0) return;

                    int idx = _listBox.SelectedIndex;
                    if (idx < _listBox.Items.Count - 1)
                    {
                        _listBox.SelectedIndex = idx + 1;
                    }
                    _listBox.ScrollIntoView(_listBox.SelectedItem);
                    ResetHorizontalScroll();
                    QueueResetHorizontalScroll();
                }
                catch (Exception ex)
                {
                    Logger.Error("SelectNext failed", ex);
                }
            });
        }

        /// <summary>
        /// Moves the selection to the previous item in the list.
        /// </summary>
        public void SelectPrevious()
        {
            EnsureUIThread(() =>
            {
                try
                {
                    if (_listBox.Items.Count == 0) return;

                    int idx = _listBox.SelectedIndex;
                    if (idx > 0)
                    {
                        _listBox.SelectedIndex = idx - 1;
                    }
                    _listBox.ScrollIntoView(_listBox.SelectedItem);
                    ResetHorizontalScroll();
                    QueueResetHorizontalScroll();
                }
                catch (Exception ex)
                {
                    Logger.Error("SelectPrevious failed", ex);
                }
            });
        }

        /// <summary>
        /// Accepts the currently selected item: dismisses the popup and returns the item.
        /// Also fires the <see cref="ItemAccepted"/> event.
        /// </summary>
        /// <returns>The selected item, or null if nothing was selected.</returns>
        public CompletionItemData AcceptSelected()
        {
            try
            {
                var item = SelectedItem;
                Dismiss();

                if (item != null)
                {
                    ItemAccepted?.Invoke(item);
                }

                return item;
            }
            catch (Exception ex)
            {
                Logger.Error("AcceptSelected failed", ex);
                Dismiss();
                return null;
            }
        }

        /// <summary>
        /// Updates the displayed items by filtering the original list with a new prefix.
        /// Preserves the popup position and keeps the best match selected.
        /// </summary>
        /// <param name="prefix">The new prefix to filter by.</param>
        public void UpdateFilter(string prefix)
        {
            EnsureUIThread(() =>
            {
                try
                {
                    PositionAtCaret();

                    if (string.IsNullOrEmpty(prefix))
                    {
                        _filteredItems = new List<CompletionItemData>(_allItems);
                    }
                    else
                    {
                        _currentPrefix = prefix;
                        _filteredItems = _allItems
                            .Where(item => MatchesFilter(item, prefix))
                            .OrderByDescending(item => CompletionItemMatcher.GetRelevance(item, prefix))
                            .ThenByDescending(item => item.Priority)
                            .ThenBy(item => CompletionItemMatcher.GetSortText(item), StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }

                    _listBox.ItemsSource = _filteredItems;

                    if (_filteredItems.Count > 0)
                    {
                        _listBox.SelectedItem = _filteredItems[0];
                        _listBox.ScrollIntoView(_listBox.SelectedItem);
                        ResetHorizontalScroll();
                        QueueResetHorizontalScroll();
                    }

                    // If no items match, hide the popup
                    if (_filteredItems.Count == 0)
                    {
                        _popup.IsOpen = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("UpdateFilter failed", ex);
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        //  LISTBOX CREATION & STYLING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a styled ListBox with virtualization enabled.
        /// </summary>
        private ListBox CreateListBox()
        {
            var listBox = new ListBox
            {
                Background = BackgroundBrush,
                Foreground = ForegroundBrush,
                BorderThickness = new Thickness(0),
                MaxHeight = PopupMaxHeight - 4, // Account for border
                Focusable = false,
                Padding = new Thickness(2),
                // Enable virtualization
                ItemsPanel = new ItemsPanelTemplate(
                    new FrameworkElementFactory(typeof(VirtualizingStackPanel)))
            };

            // Enable UI virtualization
            VirtualizingPanel.SetIsVirtualizing(listBox, true);
            VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(listBox, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Auto);

            // Custom item template (generated in code)
            listBox.ItemTemplate = CreateItemTemplate();

            // Custom style for ListBoxItem (selection colors)
            listBox.ItemContainerStyle = CreateItemContainerStyle();

            // Handle mouse double-click for acceptance
            listBox.MouseDoubleClick += OnListBoxMouseDoubleClick;

            // Single click selection
            listBox.PreviewMouseLeftButtonDown += OnListBoxPreviewMouseDown;

            return listBox;
        }

        /// <summary>
        /// Creates the data template for each completion item.
        /// Format: [Icon] Text  Description
        /// </summary>
        private DataTemplate CreateItemTemplate()
        {
            var template = new DataTemplate(typeof(CompletionItemData));

            // Root: horizontal StackPanel
            var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
            stackFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            stackFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));

            // Compact kind badge
            var badgeFactory = new FrameworkElementFactory(typeof(Border));
            badgeFactory.SetValue(Border.BackgroundProperty, IconBackgroundBrush);
            badgeFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            badgeFactory.SetValue(FrameworkElement.WidthProperty, 22.0);
            badgeFactory.SetValue(FrameworkElement.HeightProperty, 18.0);
            badgeFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 8, 1));
            badgeFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var iconFactory = new FrameworkElementFactory(typeof(TextBlock));
            iconFactory.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding("KindGlyph"));
            iconFactory.SetValue(TextBlock.ForegroundProperty, IconForegroundBrush);
            iconFactory.SetValue(TextBlock.FontSizeProperty, 10.0);
            iconFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            iconFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            iconFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            badgeFactory.AppendChild(iconFactory);
            stackFactory.AppendChild(badgeFactory);

            // Main text TextBlock
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding("Text"));
            textFactory.SetValue(TextBlock.ForegroundProperty, ForegroundBrush);
            textFactory.SetValue(TextBlock.FontSizeProperty, 12.5);
            textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            textFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            textFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            stackFactory.AppendChild(textFactory);

            // Description TextBlock (smaller, gray)
            var descFactory = new FrameworkElementFactory(typeof(TextBlock));
            descFactory.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding("Description"));
            descFactory.SetValue(TextBlock.ForegroundProperty, DescriptionBrush);
            descFactory.SetValue(TextBlock.FontSizeProperty, 11.0);
            descFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            descFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            stackFactory.AppendChild(descFactory);

            template.VisualTree = stackFactory;
            return template;
        }

        /// <summary>
        /// Creates a custom style for ListBoxItem to use dark-themed selection colors.
        /// </summary>
        private Style CreateItemContainerStyle()
        {
            var style = new Style(typeof(ListBoxItem));

            // Default background
            style.Setters.Add(new Setter(Control.BackgroundProperty, TransparentBrush));
            style.Setters.Add(new Setter(Control.ForegroundProperty, ForegroundBrush));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 3, 2, 3)));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(2, 1, 2, 1)));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 26.0));
            style.Setters.Add(new Setter(UIElement.FocusableProperty, false));

            // Selected state trigger
            var selectedTrigger = new Trigger
            {
                Property = ListBoxItem.IsSelectedProperty,
                Value = true
            };
            selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, SelectionBrush));
            selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, ForegroundBrush));
            style.Triggers.Add(selectedTrigger);

            // Mouse over trigger
            var mouseOverTrigger = new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true
            };
            mouseOverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, HoverBrush));
            style.Triggers.Add(mouseOverTrigger);

            return style;
        }

        // ═══════════════════════════════════════════════════════════════
        //  EVENT HANDLERS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles mouse double-click on a list item to accept it.
        /// </summary>
        private void OnListBoxMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                AcceptSelected();
            }
            catch (Exception ex)
            {
                Logger.Error("OnListBoxMouseDoubleClick failed", ex);
            }
        }

        /// <summary>
        /// Handles mouse-down to select items without stealing focus from the editor.
        /// </summary>
        private void OnListBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Find the ListBoxItem under the mouse
                var element = e.OriginalSource as DependencyObject;
                while (element != null && !(element is ListBoxItem))
                {
                    element = VisualTreeHelper.GetParent(element);
                }

                if (element is ListBoxItem listBoxItem)
                {
                    listBoxItem.IsSelected = true;
                    // Don't mark handled — allow double-click to fire
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OnListBoxPreviewMouseDown failed", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks if a completion item matches the given filter prefix (case-insensitive).
        /// </summary>
        private static bool MatchesFilter(CompletionItemData item, string prefix)
        {
            return CompletionItemMatcher.Matches(item, prefix);
        }

        /// <summary>
        /// Anchors the popup to the editor visual at the current caret position.
        /// </summary>
        private void PositionAtCaret()
        {
            try
            {
                if (_currentView?.VisualElement == null)
                    return;

                Point caretPos = _currentView.GetCaretPopupPosition();
                _popup.PlacementTarget = _currentView.VisualElement;
                _popup.HorizontalOffset = caretPos.X;
                _popup.VerticalOffset = caretPos.Y + 2;
            }
            catch (Exception ex)
            {
                Logger.Error("PositionAtCaret failed", ex);
            }
        }

        private void ResetHorizontalScroll()
        {
            try
            {
                _listBox.ApplyTemplate();

                var scrollViewer = FindVisualChild<ScrollViewer>(_listBox);
                scrollViewer?.ScrollToLeftEnd();
            }
            catch (Exception ex)
            {
                Logger.Warn($"ResetHorizontalScroll failed: {ex.Message}");
            }
        }

        private void QueueResetHorizontalScroll()
        {
            try
            {
                var dispatcher = _listBox.Dispatcher;
                if (dispatcher == null)
                    return;

                dispatcher.BeginInvoke(new Action(ResetHorizontalScroll), DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                Logger.Warn($"QueueResetHorizontalScroll failed: {ex.Message}");
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    return typed;

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }

        /// <summary>
        /// Ensures the given action runs on the UI thread. If already on the UI thread,
        /// executes synchronously; otherwise dispatches to the UI thread.
        /// </summary>
        private void EnsureUIThread(Action action)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher ?? _popup.Dispatcher;
                if (dispatcher == null)
                {
                    action();
                    return;
                }

                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.Invoke(action);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("EnsureUIThread failed", ex);
            }
        }

        /// <summary>
        /// Freezes all shared brushes for thread safety.
        /// </summary>
        private static void FreezeBrushes()
        {
            try
            {
                if (BackgroundBrush.CanFreeze) BackgroundBrush.Freeze();
                if (ForegroundBrush.CanFreeze) ForegroundBrush.Freeze();
                if (SelectionBrush.CanFreeze) SelectionBrush.Freeze();
                if (HoverBrush.CanFreeze) HoverBrush.Freeze();
                if (BorderBrush_.CanFreeze) BorderBrush_.Freeze();
                if (DescriptionBrush.CanFreeze) DescriptionBrush.Freeze();
                if (IconBackgroundBrush.CanFreeze) IconBackgroundBrush.Freeze();
                if (IconForegroundBrush.CanFreeze) IconForegroundBrush.Freeze();
            }
            catch
            {
                // Non-critical — brushes still work if not frozen
            }
        }

        /// <summary>
        /// Native Win32 interop for preventing focus stealing.
        /// Handles 32-bit (SSMS 18-20) and 64-bit (SSMS 21+) platforms.
        /// </summary>
        private static class NativeMethods
        {
            public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
            {
                if (IntPtr.Size == 8)
                    return GetWindowLongPtr64(hWnd, nIndex);
                else
                    return GetWindowLongPtr32(hWnd, nIndex);
            }

            public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
            {
                if (IntPtr.Size == 8)
                    return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
                else
                    return SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
            }

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
            private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
            private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
            private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
            private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        }
    }
}
