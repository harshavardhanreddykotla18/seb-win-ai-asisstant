/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SafeExamBrowser.Browser.Contracts.Events;
using SafeExamBrowser.Core.Contracts.Resources.Icons;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Settings.Browser;
using SafeExamBrowser.UserInterface.Contracts;
using SafeExamBrowser.UserInterface.Contracts.Browser;
using SafeExamBrowser.UserInterface.Contracts.Browser.Data;
using SafeExamBrowser.UserInterface.Contracts.Browser.Events;
using SafeExamBrowser.UserInterface.Contracts.Windows;
using SafeExamBrowser.UserInterface.Contracts.Windows.Events;
using SafeExamBrowser.UserInterface.Desktop.Controls.Browser;
using SafeExamBrowser.UserInterface.Shared.Utilities;
using System.Text;
using System.Net.Http;
using System.Web.Script.Serialization;
using System.IO;

namespace SafeExamBrowser.UserInterface.Desktop.Windows
{
	internal partial class BrowserWindow : Window, IBrowserWindow
	{
		private const string CLEAR_FIND_TERM = "thisisahacktoclearthesearchresultsasitappearsthatthereisnosuchfunctionalityincef";

		private readonly bool isMainWindow;
		private readonly BrowserSettings settings;
		private readonly IText text;
		private readonly ILogger logger;
		private readonly IBrowserControl browserControl;

		// AI Assistant
		private readonly HttpClient aiHttpClient =
			new HttpClient();

		private Window aiChatPopup;
		private TextBox aiQuestionBox;
		private TextBox aiAnswerBox;
		private Image aiScreenImage;
		private Button aiSendButton;
		private Button aiScreenButton;
		private Button aiChatButton;

		private WindowClosedEventHandler closed;
		private WindowClosingEventHandler closing;
		private bool browserControlGetsFocusFromTaskbar;
		private IInputElement tabKeyDownFocusElement;

		private WindowSettings WindowSettings
		{
			get { return isMainWindow ? settings.MainWindow : settings.AdditionalWindow; }
		}

		public bool CanNavigateBackwards
		{
			set => Dispatcher.Invoke(() => BackwardButton.IsEnabled = value);
		}

		public bool CanNavigateForwards
		{
			set => Dispatcher.Invoke(() => ForwardButton.IsEnabled = value);
		}

		public IntPtr Handle { get; private set; }

		public event AddressChangedEventHandler AddressChanged;
		public event ActionRequestedEventHandler BackwardNavigationRequested;
		public event ActionRequestedEventHandler DeveloperConsoleRequested;
		public event FindRequestedEventHandler FindRequested;
		public event ActionRequestedEventHandler ForwardNavigationRequested;
		public event ActionRequestedEventHandler HomeNavigationRequested;
		public event ActionRequestedEventHandler ReloadRequested;
		public event ActionRequestedEventHandler ZoomInRequested;
		public event ActionRequestedEventHandler ZoomOutRequested;
		public event ActionRequestedEventHandler ZoomResetRequested;
		public event LoseFocusRequestedEventHandler LoseFocusRequested;

		event WindowClosedEventHandler IWindow.Closed
		{
			add { closed += value; }
			remove { closed -= value; }
		}

		event WindowClosingEventHandler IWindow.Closing
		{
			add { closing += value; }
			remove { closing -= value; }
		}

		internal BrowserWindow(
			IBrowserControl browserControl,
			BrowserSettings settings,
			bool isMainWindow,
			IText text,
			ILogger logger)
		{
			this.browserControl = browserControl;
			this.isMainWindow = isMainWindow;
			this.logger = logger;
			this.settings = settings;
			this.text = text;

			InitializeComponent();
			InitializeBrowserWindow(browserControl);
		}

		public void BringToForeground()
		{
			Dispatcher.Invoke(() =>
			{
				if (WindowState == WindowState.Minimized)
				{
					WindowState = WindowState.Normal;
				}

				Activate();
			});
		}

		public new void Close()
		{
			Dispatcher.Invoke(() =>
			{
				Closing -= BrowserWindow_Closing;

				if (aiChatPopup != null)
				{
					aiChatPopup.Hide();
				}

				closing?.Invoke();
				base.Close();
			});
		}

		public void FocusToolbar(bool forward)
		{
			Dispatcher.BeginInvoke((Action) (async () =>
			{
				Activate();
				await Task.Delay(50);

				var buttons = new System.Windows.Controls.Control[]
				{
					ForwardButton,
					BackwardButton,
					ReloadButton,
					UrlTextBox,
					MenuButton,
				};

				for (
					var i = forward ? 0 : buttons.Length - 1;
					i >= 0 && i < buttons.Length;
					i += forward ? 1 : -1)
				{
					if (buttons[i].IsEnabled &&
						buttons[i].Visibility == Visibility.Visible)
					{
						buttons[i].Focus();
						break;
					}
				}
			}));
		}

		public void FocusBrowser()
		{
			Dispatcher.BeginInvoke((Action) (async () =>
			{
				FocusToolbar(false);
				await Task.Delay(100);

				browserControlGetsFocusFromTaskbar = true;

				var focusedElement =
					FocusManager.GetFocusedElement(this) as UIElement;

				focusedElement.MoveFocus(
					new TraversalRequest(
						FocusNavigationDirection.Right));

				await Task.Delay(150);

				browserControlGetsFocusFromTaskbar = false;
			}));
		}

		public void FocusAddressBar()
		{
			Dispatcher.BeginInvoke((Action) (() =>
			{
				UrlTextBox.Focus();
			}));
		}

		public new void Hide()
		{
			Dispatcher.Invoke(base.Hide);
		}

		public new void Show()
		{
			Dispatcher.Invoke(base.Show);
		}

		public void ShowFindbar()
		{
			Dispatcher.InvokeAsync(() =>
			{
				Findbar.Visibility = Visibility.Visible;
				FindTextBox.Focus();
			});
		}

		public void UpdateAddress(string url)
		{
			Dispatcher.Invoke(() => UrlTextBox.Text = url);
		}

		public void UpdateIcon(IconResource icon)
		{
			Dispatcher.InvokeAsync(() =>
			{
				if (icon is BitmapIconResource bitmap)
				{
					Icon = new BitmapImage(bitmap.Uri);
				}
			});
		}

		public void UpdateDownloadState(DownloadItemState state)
		{
			Dispatcher.InvokeAsync(() =>
			{
				var control = Downloads.Children
					.OfType<DownloadItemControl>()
					.FirstOrDefault(c => c.Id == state.Id);

				if (control == default)
				{
					control = new DownloadItemControl(
						state.Id,
						text);

					Downloads.Children.Add(control);
				}

				control.Update(state);

				DownloadsButton.Visibility = Visibility.Visible;
				DownloadsPopup.IsOpen = IsActive;
			});
		}

		public void UpdateLoadingState(bool isLoading)
		{
			Dispatcher.Invoke(() =>
				ProgressBar.Visibility =
					isLoading
						? Visibility.Visible
						: Visibility.Hidden);
		}

		public void UpdateProgress(double value)
		{
			Dispatcher.Invoke(() =>
				ProgressBar.Value = value * 100);
		}

		public void UpdateTitle(string title)
		{
			Dispatcher.Invoke(() => Title = title);
		}

		public void UpdateZoomLevel(double value)
		{
			Dispatcher.Invoke(() =>
			{
				ZoomLevel.Text = $"{value}%";

				var zoomButtonName =
					text.Get(TextKey.BrowserWindow_ZoomLevelReset)
						.Replace("%%ZOOM%%", value.ToString("0"));

				ZoomResetButton.SetValue(
					System.Windows.Automation.AutomationProperties.NameProperty,
					zoomButtonName);
			});
		}

		private void BrowserWindow_Closing(
			object sender,
			CancelEventArgs e)
		{
			if (isMainWindow)
			{
				e.Cancel = true;
			}
			else
			{
				closing?.Invoke();
			}
		}

		private void BrowserWindow_KeyDown(
			object sender,
			KeyEventArgs e)
		{
			if (e.Key == Key.Tab)
			{
				var hasShift =
					(Keyboard.Modifiers & ModifierKeys.Shift)
					== ModifierKeys.Shift;

				if (Toolbar.IsKeyboardFocusWithin && hasShift)
				{
					var firstActiveElementInToolbar =
						Toolbar.PredictFocus(
							FocusNavigationDirection.Right);

					if (firstActiveElementInToolbar is UIElement)
					{
						var control =
							firstActiveElementInToolbar as UIElement;

						if (control.IsKeyboardFocusWithin)
						{
							LoseFocusRequested?.Invoke(false);
							e.Handled = true;
						}
					}
				}

				tabKeyDownFocusElement =
					FocusManager.GetFocusedElement(this);
			}
			else
			{
				tabKeyDownFocusElement = null;
			}
		}

		private void BrowserWindow_KeyUp(
			object sender,
			KeyEventArgs e)
		{
			if (e.Key == Key.F5)
			{
				ReloadRequested?.Invoke();
			}

			if (e.Key == Key.Home)
			{
				HomeNavigationRequested?.Invoke();
			}

			if (settings.AllowFind &&
				(Keyboard.IsKeyDown(Key.LeftCtrl) ||
				 Keyboard.IsKeyDown(Key.RightCtrl)) &&
				e.Key == Key.F)
			{
				ShowFindbar();
			}

			if (e.Key == Key.Tab)
			{
				var hasCtrl =
					(Keyboard.Modifiers & ModifierKeys.Control)
					== ModifierKeys.Control;

				var hasShift =
					(Keyboard.Modifiers & ModifierKeys.Shift)
					== ModifierKeys.Shift;

				if (BrowserControlHost.IsFocused && hasCtrl)
				{
					if (Findbar.Visibility == Visibility.Hidden ||
						hasShift)
					{
						Toolbar.Focus();
					}
					else if (Toolbar.Visibility == Visibility.Hidden)
					{
						Findbar.Focus();
					}
				}
				else if (MenuPopup.IsKeyboardFocusWithin)
				{
					var focusedElement =
						FocusManager.GetFocusedElement(this);

					if (focusedElement is Control focusedControl &&
						tabKeyDownFocusElement is Control prevFocusedControl)
					{
						if (!hasShift &&
							focusedControl.TabIndex <
							prevFocusedControl.TabIndex)
						{
							MenuPopup.IsOpen = false;
							FocusBrowser();
						}
						else if (
							hasShift &&
							focusedControl.TabIndex >
							prevFocusedControl.TabIndex)
						{
							MenuPopup.IsOpen = false;
							MenuButton.Focus();
						}
					}
				}
			}

			if (e.Key == Key.Escape &&
				MenuPopup.IsOpen)
			{
				MenuPopup.IsOpen = false;
				MenuButton.Focus();
			}
		}

		private void BrowserWindow_Loaded(
			object sender,
			RoutedEventArgs e)
		{
			Handle = new WindowInteropHelper(this).Handle;

			if (isMainWindow)
			{
				this.DisableCloseButton();
			}
		}

		private void FindbarCloseButton_Click(
			object sender,
			RoutedEventArgs e)
		{
			FindRequested?.Invoke(
				CLEAR_FIND_TERM,
				true,
				false);

			Findbar.Visibility =
				Visibility.Collapsed;
		}

		private void FindNextButton_Click(
			object sender,
			RoutedEventArgs e)
		{
			FindRequested?.Invoke(
				FindTextBox.Text,
				false,
				FindCaseSensitiveCheckBox.IsChecked == true);
		}

		private void FindPreviousButton_Click(
			object sender,
			RoutedEventArgs e)
		{
			FindRequested?.Invoke(
				FindTextBox.Text,
				false,
				FindCaseSensitiveCheckBox.IsChecked == true,
				false);
		}

		private void FindTextBox_KeyUp(
			object sender,
			KeyEventArgs e)
		{
			if (string.IsNullOrEmpty(FindTextBox.Text))
			{
				FindRequested?.Invoke(
					CLEAR_FIND_TERM,
					true,
					false);
			}
			else if (e.Key == Key.Enter)
			{
				FindRequested?.Invoke(
					FindTextBox.Text,
					false,
					FindCaseSensitiveCheckBox.IsChecked == true);
			}
			else
			{
				FindRequested?.Invoke(
					FindTextBox.Text,
					true,
					FindCaseSensitiveCheckBox.IsChecked == true);
			}
		}

		private CustomPopupPlacement[] Popup_PlacementCallback(
			Size popupSize,
			Size targetSize,
			Point offset)
		{
			return new[]
			{
				new CustomPopupPlacement(
					new Point(
						targetSize.Width -
						Toolbar.Margin.Right -
						popupSize.Width,
						-2),
					PopupPrimaryAxis.None)
			};
		}

		private CustomPopupPlacement[] AIAssistantPopup_PlacementCallback(
			Size popupSize,
			Size targetSize,
			Point offset)
		{
			return new[]
			{
				new CustomPopupPlacement(
					new Point(
						targetSize.Width -
						popupSize.Width -
						25,
						targetSize.Height -
						popupSize.Height -
						25),
					PopupPrimaryAxis.None)
			};
		}

		private void SystemParameters_StaticPropertyChanged(
			object sender,
			PropertyChangedEventArgs e)
		{
			if (e.PropertyName ==
				nameof(SystemParameters.WorkArea))
			{
				Dispatcher.InvokeAsync(InitializeBounds);
			}
		}

		private void UrlTextBox_GotMouseCapture(
			object sender,
			MouseEventArgs e)
		{
			if (UrlTextBox.Tag as bool? != true)
			{
				UrlTextBox.SelectAll();
				UrlTextBox.Tag = true;
			}
		}

		private void UrlTextBox_KeyUp(
			object sender,
			KeyEventArgs e)
		{
			if (e.Key == Key.Enter)
			{
				AddressChanged?.Invoke(
					UrlTextBox.Text);
			}
		}

		private void InitializeBrowserWindow(
			IBrowserControl browserControl)
		{
			if (browserControl.EmbeddableControl
				is System.Windows.Forms.Control control)
			{
				BrowserControlHost.Child = control;
			}

			RegisterEvents();
			InitializeBounds();
			ApplySettings();
			LoadIcons();
			LoadText();
		}

		private void RegisterEvents()
		{
			BackwardButton.Click +=
				(o, args) =>
					BackwardNavigationRequested?.Invoke();

			Closed +=
				(o, args) =>
					closed?.Invoke();

			Closing += BrowserWindow_Closing;

			DeveloperConsoleButton.Click +=
				(o, args) =>
					DeveloperConsoleRequested?.Invoke();

			DownloadsButton.Click +=
				(o, args) =>
					DownloadsPopup.IsOpen =
						!DownloadsPopup.IsOpen;

			DownloadsButton.MouseLeave += (o, args) =>
				Task.Delay(250).ContinueWith(_ =>
					Dispatcher.Invoke(() =>
						DownloadsPopup.IsOpen =
							DownloadsPopup.IsMouseOver));

			DownloadsPopup.CustomPopupPlacementCallback =
				new CustomPopupPlacementCallback(
					Popup_PlacementCallback);

			DownloadsPopup.MouseLeave += (o, args) =>
				Task.Delay(250).ContinueWith(_ =>
					Dispatcher.Invoke(() =>
						DownloadsPopup.IsOpen =
							DownloadsPopup.IsMouseOver));

			FindbarCloseButton.Click +=
				FindbarCloseButton_Click;

			FindNextButton.Click +=
				FindNextButton_Click;

			FindPreviousButton.Click +=
				FindPreviousButton_Click;

			FindMenuButton.Click +=
				(o, args) =>
					ShowFindbar();

			FindTextBox.KeyUp +=
				FindTextBox_KeyUp;

			ForwardButton.Click +=
				(o, args) =>
					ForwardNavigationRequested?.Invoke();

			HomeButton.Click +=
				(o, args) =>
					HomeNavigationRequested?.Invoke();

			Loaded += BrowserWindow_Loaded;

			AIAssistantPopup.CustomPopupPlacementCallback =
				new CustomPopupPlacementCallback(
					AIAssistantPopup_PlacementCallback);

			Loaded += (o, args) =>
			{
				AIAssistantPopup.IsOpen = true;
			};

			MenuButton.Click +=
				MenuButton_Click;

			MenuPopup.CustomPopupPlacementCallback =
				new CustomPopupPlacementCallback(
					Popup_PlacementCallback);

			MenuPopup.LostFocus += (o, args) =>
				Task.Delay(250).ContinueWith(_ =>
					Dispatcher.Invoke(() =>
						MenuPopup.IsOpen =
							MenuPopup.IsKeyboardFocusWithin));

			KeyDown += BrowserWindow_KeyDown;
			KeyUp += BrowserWindow_KeyUp;

			LocationChanged += (o, args) =>
			{
				DownloadsPopup.IsOpen = false;
				MenuPopup.IsOpen = false;
			};

			ReloadButton.Click +=
				(o, args) =>
					ReloadRequested?.Invoke();

			SizeChanged += (o, args) =>
			{
				DownloadsPopup.IsOpen = false;
				MenuPopup.IsOpen = false;
			};

			SystemParameters.StaticPropertyChanged +=
				SystemParameters_StaticPropertyChanged;

			UrlTextBox.GotKeyboardFocus +=
				(o, args) =>
					UrlTextBox.SelectAll();

			UrlTextBox.GotMouseCapture +=
				UrlTextBox_GotMouseCapture;

			UrlTextBox.LostKeyboardFocus +=
				(o, args) =>
					UrlTextBox.Tag = null;

			UrlTextBox.LostFocus +=
				(o, args) =>
					UrlTextBox.Tag = null;

			UrlTextBox.KeyUp +=
				UrlTextBox_KeyUp;

			UrlTextBox.MouseDoubleClick +=
				(o, args) =>
					UrlTextBox.SelectAll();

			ZoomInButton.Click +=
				(o, args) =>
					ZoomInRequested?.Invoke();

			ZoomOutButton.Click +=
				(o, args) =>
					ZoomOutRequested?.Invoke();

			ZoomResetButton.Click +=
				(o, args) =>
					ZoomResetRequested?.Invoke();

			BrowserControlHost.GotKeyboardFocus +=
				BrowserControlHost_GotKeyboardFocus;
		}

		private void MenuButton_Click(
			object sender,
			RoutedEventArgs e)
		{
			MenuPopup.IsOpen =
				!MenuPopup.IsOpen;

			ZoomInButton.Focus();
		}

		private void BrowserControlHost_GotKeyboardFocus(
			object sender,
			KeyboardFocusChangedEventArgs e)
		{
			var forward =
				!browserControlGetsFocusFromTaskbar;

			var javascript = @"
if (typeof __SEB_focusElement === 'undefined') {
  __SEB_focusElement = function (forward) {
	if (!document.body)
		return;
	var items = [].map
	  .call(document.body.querySelectorAll(['input', 'select', 'a[href]', 'textarea', 'button', '[tabindex]']), function(el, i) { return { el, i } })
	  .filter(function(e) { return e.el.tabIndex >= 0 && !e.el.disabled && e.el.offsetParent; })
	  .sort(function(a,b) { return a.el.tabIndex === b.el.tabIndex ? a.i - b.i : (a.el.tabIndex || 9E9) - (b.el.tabIndex || 9E9); })
	var item = items[forward ? 1 : items.length - 1];
	if (item && item.focus && typeof item.focus !== 'function')
		throw ('item.focus is not a function, ' + typeof item.focus)
	setTimeout(function () { item && item.focus && item.focus(); }, 20);
  }
}";

			browserControl.ExecuteJavaScript(
				javascript,
				result =>
				{
					if (!result.Success)
					{
						logger.Warn(
							$"Failed to initialize JavaScript: {result.Message}");
					}
				});

			browserControl.ExecuteJavaScript(
				"__SEB_focusElement(" +
				forward.ToString().ToLower() +
				")",
				result =>
				{
					if (!result.Success)
					{
						logger.Warn(
							$"Failed to execute JavaScript: {result.Message}");
					}
				});
		}

		private void ApplySettings()
		{
			BackwardButton.IsEnabled =
				WindowSettings.AllowBackwardNavigation;

			BackwardButton.Visibility =
				WindowSettings.AllowBackwardNavigation
					? Visibility.Visible
					: Visibility.Collapsed;

			DeveloperConsoleMenuItem.Visibility =
				WindowSettings.AllowDeveloperConsole
					? Visibility.Visible
					: Visibility.Collapsed;

			FindMenuItem.Visibility =
				settings.AllowFind
					? Visibility.Visible
					: Visibility.Collapsed;

			ForwardButton.IsEnabled =
				WindowSettings.AllowForwardNavigation;

			ForwardButton.Visibility =
				WindowSettings.AllowForwardNavigation
					? Visibility.Visible
					: Visibility.Collapsed;

			HomeButton.IsEnabled =
				WindowSettings.ShowHomeButton;

			HomeButton.Visibility =
				WindowSettings.ShowHomeButton
					? Visibility.Visible
					: Visibility.Collapsed;

			ReloadButton.IsEnabled =
				WindowSettings.AllowReloading;

			ReloadButton.Visibility =
				WindowSettings.ShowReloadButton
					? Visibility.Visible
					: Visibility.Collapsed;

			Toolbar.Visibility =
				WindowSettings.ShowToolbar
					? Visibility.Visible
					: Visibility.Collapsed;

			UrlTextBox.Visibility =
				WindowSettings.AllowAddressBar
					? Visibility.Visible
					: Visibility.Hidden;

			ZoomMenuItem.Visibility =
				settings.AllowPageZoom
					? Visibility.Visible
					: Visibility.Collapsed;
		}

		private void InitializeBounds()
		{
			if (isMainWindow &&
				WindowSettings.FullScreenMode)
			{
				Top = 0;
				Left = 0;
				Height = SystemParameters.WorkArea.Height;
				Width = SystemParameters.WorkArea.Width;
				ResizeMode = ResizeMode.NoResize;
				WindowStyle = WindowStyle.None;
			}
			else if (
				WindowSettings.RelativeHeight == 100 &&
				WindowSettings.RelativeWidth == 100)
			{
				WindowState = WindowState.Maximized;
			}
			else
			{
				if (WindowSettings.RelativeHeight > 0)
				{
					Height =
						SystemParameters.WorkArea.Height *
						WindowSettings.RelativeHeight.Value /
						100;

					Top =
						(SystemParameters.WorkArea.Height / 2) -
						(Height / 2);
				}
				else if (WindowSettings.AbsoluteHeight > 0)
				{
					Height =
						this.TransformFromPhysical(
							0,
							WindowSettings.AbsoluteHeight.Value).Y;

					Top =
						(SystemParameters.WorkArea.Height / 2) -
						(Height / 2);
				}

				if (WindowSettings.RelativeWidth > 0)
				{
					Width =
						SystemParameters.WorkArea.Width *
						WindowSettings.RelativeWidth.Value /
						100;
				}
				else if (WindowSettings.AbsoluteWidth > 0)
				{
					Width =
						this.TransformFromPhysical(
							WindowSettings.AbsoluteWidth.Value,
							0).X;
				}

				if (Height >
					SystemParameters.WorkArea.Height)
				{
					Top = 0;
					Height =
						SystemParameters.WorkArea.Height;
				}

				if (Width >
					SystemParameters.WorkArea.Width)
				{
					Left = 0;
					Width =
						SystemParameters.WorkArea.Width;
				}

				switch (WindowSettings.Position)
				{
					case WindowPosition.Left:
						Left = 0;
						break;

					case WindowPosition.Center:
						Left =
							(SystemParameters.WorkArea.Width / 2) -
							(Width / 2);
						break;

					case WindowPosition.Right:
						Left =
							SystemParameters.WorkArea.Width -
							Width;
						break;
				}
			}
		}

		private void LoadIcons()
		{
			var backward = new XamlIconResource
			{
				Uri = new Uri(
					"pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/NavigateBack.xaml")
			};

			var forward = new XamlIconResource
			{
				Uri = new Uri(
					"pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/NavigateForward.xaml")
			};

			var home = new XamlIconResource
			{
				Uri = new Uri(
					"pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Home.xaml")
			};

			var menu = new XamlIconResource
			{
				Uri = new Uri(
					"pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Menu.xaml")
			};

			var reload = new XamlIconResource
			{
				Uri = new Uri(
					"pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Reload.xaml")
			};

			BackwardButton.Content =
				IconResourceLoader.Load(backward);

			ForwardButton.Content =
				IconResourceLoader.Load(forward);

			HomeButton.Content =
				IconResourceLoader.Load(home);

			MenuButton.Content =
				IconResourceLoader.Load(menu);

			ReloadButton.Content =
				IconResourceLoader.Load(reload);
		}

		private void LoadText()
		{
			DeveloperConsoleText.Text =
				text.Get(
					TextKey.BrowserWindow_DeveloperConsoleMenuItem);

			DeveloperConsoleButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_DeveloperConsoleMenuItem));

			FindCaseSensitiveCheckBox.Content =
				text.Get(
					TextKey.BrowserWindow_FindCaseSensitive);

			FindMenuText.Text =
				text.Get(
					TextKey.BrowserWindow_FindMenuItem);

			FindMenuButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_FindMenuItem));

			ZoomText.Text =
				text.Get(
					TextKey.BrowserWindow_ZoomMenuItem);

			ZoomInButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_ZoomMenuPlus));

			ZoomOutButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_ZoomMenuMinus));

			ReloadButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_ReloadButton));

			BackwardButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_BackwardButton));

			ForwardButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_ForwardButton));

			DownloadsButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_DownloadsButton));

			HomeButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_HomeButton));

			MenuButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_MenuButton));

			UrlTextBox.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_UrlTextBox));

			FindTextBox.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_SearchTextBox));

			FindPreviousButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_SearchPrevious));

			FindNextButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_SearchNext));

			FindbarCloseButton.SetValue(
				System.Windows.Automation.AutomationProperties.NameProperty,
				text.Get(
					TextKey.BrowserWindow_CloseButton));
		}

		// ============================================================
		// AI ASSISTANT
		// ============================================================

		private void AIAssistantButton_Click(
			object sender,
			RoutedEventArgs e)
		{
			OpenAIChat();
		}

		private void OpenAIChat()
		{
			if (aiChatPopup == null)
			{
				var panel = new Border
				{
					Width = 420,
					Height = 560,
					Background = new SolidColorBrush(Color.FromArgb(238, 255, 255, 255)),
					BorderBrush = new SolidColorBrush(Color.FromArgb(150, 180, 180, 180)),
					BorderThickness = new Thickness(1),
					CornerRadius = new CornerRadius(16),
					Padding = new Thickness(16),
					Focusable = false
				};

				var layout = new Grid();
				layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
				layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

				var header = new Grid { Margin = new Thickness(2, 0, 2, 12) };
				header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

				var titleStack = new StackPanel();
				titleStack.Children.Add(new TextBlock
				{
					Text = "AI Assistant",
					Foreground = Brushes.Black,
					FontSize = 19,
					FontWeight = FontWeights.SemiBold
				});
				titleStack.Children.Add(new TextBlock
				{
					Text = "Chat + local screen preview",
					Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
					FontSize = 11,
					Margin = new Thickness(0, 3, 0, 0)
				});
				Grid.SetColumn(titleStack, 0);
				header.Children.Add(titleStack);

				var closeButton = new Button
				{
					Content = "×",
					Width = 32,
					Height = 32,
					FontSize = 20,
					Foreground = Brushes.Black,
					Background = new SolidColorBrush(Color.FromArgb(145, 245, 245, 245)),
					BorderThickness = new Thickness(0)
				};
				closeButton.Click += (o, args) => aiChatPopup.Hide();
				Grid.SetColumn(closeButton, 1);
				header.Children.Add(closeButton);
				Grid.SetRow(header, 0);
				layout.Children.Add(header);

				var modeGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
				modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

				aiChatButton = new Button
				{
					Content = "Chat",
					Height = 38,
					Margin = new Thickness(0, 0, 5, 0),
					Foreground = Brushes.Black,
					Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
					BorderThickness = new Thickness(0)
				};

				aiScreenButton = new Button
				{
					Content = "Screen Preview",
					Height = 38,
					Margin = new Thickness(5, 0, 0, 0),
					Foreground = Brushes.Black,
					Background = new SolidColorBrush(Color.FromArgb(145, 245, 245, 245)),
					BorderThickness = new Thickness(0)
				};

				aiChatButton.Click += (o, args) =>
				{
					aiChatButton.Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
					aiScreenButton.Background = new SolidColorBrush(Color.FromArgb(145, 245, 245, 245));
					FocusAIQuestionBox();
				};

				aiScreenButton.Click += async (o, args) => await AnalyzeScreen();

				Grid.SetColumn(aiChatButton, 0);
				Grid.SetColumn(aiScreenButton, 1);
				modeGrid.Children.Add(aiChatButton);
				modeGrid.Children.Add(aiScreenButton);
				Grid.SetRow(modeGrid, 1);
				layout.Children.Add(modeGrid);

				var answerBorder = new Border
				{
					Background = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255)),
					BorderBrush = new SolidColorBrush(Color.FromArgb(130, 190, 190, 190)),
					BorderThickness = new Thickness(1),
					CornerRadius = new CornerRadius(12),
					Padding = new Thickness(12),
					Margin = new Thickness(0, 0, 0, 12)
				};

				var answerGrid = new Grid();

				aiScreenImage = new Image
				{
					Stretch = Stretch.Uniform,
					Visibility = Visibility.Collapsed,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				};

				aiAnswerBox = new TextBox
				{
					IsReadOnly = true,
					TextWrapping = TextWrapping.Wrap,
					AcceptsReturn = true,
					VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
					HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
					Background = Brushes.Transparent,
					Foreground = Brushes.Black,
					BorderThickness = new Thickness(0),
					Padding = new Thickness(0),
					FontFamily = new System.Windows.Media.FontFamily("Consolas"),
					FontSize = 14,
					Text = "Ask a question to start."
				};

				answerGrid.Children.Add(aiScreenImage);
				answerGrid.Children.Add(aiAnswerBox);

				answerBorder.Child = answerGrid;
				Grid.SetRow(answerBorder, 2);
				layout.Children.Add(answerBorder);

				var inputGrid = new Grid();
				inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

				aiQuestionBox = new TextBox
				{
					Height = 44,
					Padding = new Thickness(12, 0, 12, 0),
					VerticalContentAlignment = VerticalAlignment.Center,
					Background = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255)),
					Foreground = Brushes.Black,
					BorderBrush = new SolidColorBrush(Color.FromArgb(150, 180, 180, 180)),
					BorderThickness = new Thickness(1),
					Focusable = true
				};
				aiQuestionBox.KeyDown += AIQuestionBox_KeyDown;
				Grid.SetColumn(aiQuestionBox, 0);
				inputGrid.Children.Add(aiQuestionBox);

				aiSendButton = new Button
				{
					Content = "Send",
					Width = 72,
					Height = 44,
					Margin = new Thickness(8, 0, 0, 0),
					Foreground = Brushes.Black,
					Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
					BorderThickness = new Thickness(0)
				};
				aiSendButton.Click += AISendButton_Click;
				Grid.SetColumn(aiSendButton, 1);
				inputGrid.Children.Add(aiSendButton);
				Grid.SetRow(inputGrid, 3);
				layout.Children.Add(inputGrid);

				panel.Child = layout;

				aiChatPopup = new Window
				{
					Content = panel,
					Width = 420,
					Height = 560,
					WindowStyle = WindowStyle.None,
					ResizeMode = ResizeMode.NoResize,
					AllowsTransparency = true,
					Background = Brushes.Transparent,
					ShowInTaskbar = false,
					Topmost = true,
					ShowActivated = true,
					Owner = this
				};
			}

			if (aiChatPopup.Visibility == Visibility.Visible)
			{
				aiChatPopup.Hide();
			}
			else
			{
				PositionAIAssistantWindow();
				aiChatPopup.Show();
				aiChatPopup.Activate();
				FocusAIQuestionBox();
			}
		}

		private void PositionAIAssistantWindow()
		{
			if (aiChatPopup == null)
			{
				return;
			}

			aiChatPopup.Left = Left + ActualWidth - aiChatPopup.Width - 24;
			aiChatPopup.Top = Top + ActualHeight - aiChatPopup.Height - 24;
		}

		private void FocusAIQuestionBox()
		{
			if (aiQuestionBox == null || !aiQuestionBox.IsEnabled)
			{
				return;
			}

			aiQuestionBox.Focus();
			Keyboard.Focus(aiQuestionBox);
			aiQuestionBox.CaretIndex = aiQuestionBox.Text.Length;
		}

		private async void AISendButton_Click(
			object sender,
			RoutedEventArgs e)
		{
			await SendAIQuestion();
		}

		private async void AIQuestionBox_KeyDown(
			object sender,
			KeyEventArgs e)
		{
			if (e.Key == Key.Enter && !e.IsRepeat)
			{
				e.Handled = true;
				await SendAIQuestion();
			}
		}

		private async Task SendAIQuestion()
		{
			var question = aiQuestionBox?.Text.Trim();

			if (string.IsNullOrWhiteSpace(question))
			{
				FocusAIQuestionBox();
				return;
			}

			aiSendButton.IsEnabled = false;
			aiQuestionBox.IsEnabled = false;
			aiScreenButton.IsEnabled = false;
			aiChatButton.IsEnabled = false;
			aiAnswerBox.Text = "Thinking...";

			try
			{
				var requestData = new System.Collections.Generic.Dictionary<string, string>
				{
					{ "question", question }
				};

				var serializer = new JavaScriptSerializer();
				var json = serializer.Serialize(requestData);

				using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
				{
					using (var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:3000/ask"))
					{
						request.Content = content;
						using (var response = await aiHttpClient.SendAsync(request))
						{
							var responseText = await response.Content.ReadAsStringAsync();

							if (!response.IsSuccessStatusCode)
							{
								aiAnswerBox.Text = "AI server error.\n\n" + responseText;
								return;
							}

							var result = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(responseText);

							if (result != null && result.ContainsKey("answer"))
							{
								aiAnswerBox.Text = Convert.ToString(result["answer"]);
							}
							else
							{
								aiAnswerBox.Text = "No answer was returned by the AI.";
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				logger.Error("Failed to communicate with AI Assistant.", ex);
				aiAnswerBox.Text =
					"Could not connect to the AI backend.\n\n" +
					"Make sure http://localhost:3000 is running.\n\n" +
					ex.Message;
			}
			finally
			{
				aiSendButton.IsEnabled = true;
				aiQuestionBox.IsEnabled = true;
				aiScreenButton.IsEnabled = true;
				aiChatButton.IsEnabled = true;
				FocusAIQuestionBox();
			}
		}

		private async Task AnalyzeScreen()
		{
			aiScreenButton.IsEnabled = false;
			aiChatButton.IsEnabled = false;
			aiQuestionBox.IsEnabled = false;
			aiSendButton.IsEnabled = false;

			aiScreenImage.Visibility = Visibility.Collapsed;
			aiAnswerBox.Visibility = Visibility.Visible;
			aiAnswerBox.Text = "Capturing the SEB window...";

			var wasPopupVisible = aiChatPopup != null && aiChatPopup.IsVisible;

			try
			{
				// IMPORTANT: obtain the WPF window handle on the UI thread.
				// Do not access WindowInteropHelper/this from inside Task.Run().
				var windowHandle = new WindowInteropHelper(this).Handle;

				if (windowHandle == IntPtr.Zero)
				{
					throw new InvalidOperationException(
						"The SEB window handle is not ready.");
				}

				// Hide the AI popup temporarily so it cannot appear in the SEB screenshot.
				if (wasPopupVisible)
				{
					aiChatPopup.Hide();
				}

				// Give Windows a moment to repaint after hiding the popup.
				await Task.Delay(150);

				// The background operation receives only the native HWND.
				// It does not touch any WPF object owned by the UI thread.
				var filePath = await Task.Run(
					() => CapturePrimaryScreenToFile(windowHandle));

				var bitmap = new BitmapImage();

				bitmap.BeginInit();
				bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
				bitmap.CacheOption = BitmapCacheOption.OnLoad;
				bitmap.EndInit();
				bitmap.Freeze();

				aiScreenImage.Source = bitmap;
				aiAnswerBox.Visibility = Visibility.Collapsed;
				aiScreenImage.Visibility = Visibility.Visible;
			}
			catch (Exception ex)
			{
				logger.Error("Failed to capture the SEB window locally.", ex);

				aiScreenImage.Visibility = Visibility.Collapsed;
				aiAnswerBox.Visibility = Visibility.Visible;
				aiAnswerBox.Text =
					"Local SEB screen capture failed.\n\n" +
					ex.Message;
			}
			finally
			{
				if (wasPopupVisible && aiChatPopup != null)
				{
					PositionAIAssistantWindow();
					aiChatPopup.Show();
					aiChatPopup.Activate();
				}

				aiScreenButton.IsEnabled = true;
				aiChatButton.IsEnabled = true;
				aiQuestionBox.IsEnabled = true;
				aiSendButton.IsEnabled = true;
			}
		}

		[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
		private struct NativeRect
		{
			public int Left;
			public int Top;
			public int Right;
			public int Bottom;
		}

		[System.Runtime.InteropServices.DllImport("user32.dll")]
		private static extern bool GetWindowRect(
			IntPtr hWnd,
			out NativeRect lpRect);

		private string CapturePrimaryScreenToFile(IntPtr windowHandle)
		{
			// This method may run on a background thread.
			// It must use only the native HWND and GDI APIs here.
			if (windowHandle == IntPtr.Zero)
			{
				throw new InvalidOperationException("The SEB window handle is not ready.");
			}

			NativeRect rect;

			if (!GetWindowRect(windowHandle, out rect))
			{
				throw new InvalidOperationException("Could not determine the SEB window bounds.");
			}

			var width = rect.Right - rect.Left;
			var height = rect.Bottom - rect.Top;

			if (width <= 0 || height <= 0)
			{
				throw new InvalidOperationException("The SEB window has an invalid size.");
			}

			var directory = Path.Combine(
				Path.GetTempPath(),
				"SEB-AI-Assistant");
			Directory.CreateDirectory(directory);

			var filePath = Path.Combine(
				directory,
				"seb-screen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jpg");

			using (var bitmap = new System.Drawing.Bitmap(
				width,
				height,
				System.Drawing.Imaging.PixelFormat.Format24bppRgb))
			{
				using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
				{
					graphics.CopyFromScreen(
						rect.Left,
						rect.Top,
						0,
						0,
						bitmap.Size,
						System.Drawing.CopyPixelOperation.SourceCopy);
				}

				bitmap.Save(
					filePath,
					System.Drawing.Imaging.ImageFormat.Jpeg);
			}

			return filePath;
		}
	}

}
