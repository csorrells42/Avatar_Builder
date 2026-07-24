using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Identity;

namespace AvatarBuilder;

public partial class IdentityReviewWindow : Window
{
	private readonly PersonIdentityMemory _memory;

	private IReadOnlyList<PersonIdentityReviewItem> _items = [];

	public IdentityReviewWindow(PersonIdentityMemory memory)
	{
		_memory = memory ?? throw new ArgumentNullException(nameof(memory));
		InitializeComponent();
		PermissionLevelComboBox.SelectedIndex = 0;
		RefreshItems();
	}

	private PersonIdentityReviewItem? Selected =>
		IdentityList.SelectedItem as PersonIdentityReviewItem;

	private void RefreshItems(string? selectIdentityId = null)
	{
		_items = _memory.GetIdentityReviewItems();
		IdentityList.ItemsSource = _items;
		if (_items.Count == 0)
		{
			StatusText.Text =
				"No retained identities yet. A person appears here after " +
				"a sustained, coherent encounter.";
			ClearEditor();
			return;
		}
		IdentityList.SelectedItem =
			_items.FirstOrDefault(item =>
				string.Equals(
					item.IdentityId,
					selectIdentityId,
					StringComparison.OrdinalIgnoreCase))
			?? _items[0];
		StatusText.Text =
			$"{_items.Count} learned " +
			(_items.Count == 1 ? "identity" : "identities") +
			" available for review.";
	}

	private void IdentitySelectionChanged(
		object sender,
		SelectionChangedEventArgs e)
	{
		PersonIdentityReviewItem? item = Selected;
		if (item is null)
		{
			ClearEditor();
			return;
		}
		DisplayNameTextBox.Text = item.DisplayName;
		RegisterAsUserCheckBox.IsChecked = item.IsRegisteredUser;
		PermissionLevelComboBox.SelectedIndex =
			string.Equals(
				item.PermissionLevel,
				"Superuser",
				StringComparison.OrdinalIgnoreCase)
				? 1
				: 0;
		PermissionLevelComboBox.IsEnabled = item.IsRegisteredUser;
		IdentityDetailText.Text =
			$"First seen: {item.FirstSeenAtUtc.ToLocalTime():g}\n" +
			$"Last seen: {item.LastSeenAtUtc.ToLocalTime():g}\n" +
			$"Observations: {item.ObservationCount:n0}\n" +
			$"Encounters: {item.EncounterCount:n0}\n" +
			(string.IsNullOrWhiteSpace(item.AvatarProfileId)
				? "Avatar profile: not linked"
				: $"Avatar profile: {item.AvatarProfileId}");
		LoadPhoto(item.ContextPhotoPath);
	}

	private void RegistrationChanged(
		object sender,
		RoutedEventArgs e)
	{
		if (PermissionLevelComboBox is not null)
		{
			PermissionLevelComboBox.IsEnabled =
				RegisterAsUserCheckBox.IsChecked == true;
			if (RegisterAsUserCheckBox.IsChecked != true)
			{
				PermissionLevelComboBox.SelectedIndex = 0;
			}
		}
	}

	private void SaveIdentityClicked(
		object sender,
		RoutedEventArgs e)
	{
		PersonIdentityReviewItem? selected = Selected;
		if (selected is null)
		{
			StatusText.Text = "Select a learned identity first.";
			return;
		}
		string displayName = DisplayNameTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(displayName))
		{
			StatusText.Text = "A name is required.";
			DisplayNameTextBox.Focus();
			return;
		}
		string permission =
			(PermissionLevelComboBox.SelectedItem as ComboBoxItem)
				?.Content?.ToString()
			?? "Default User";
		bool saved = _memory.UpdateIdentityReview(
			selected.IdentityId,
			displayName,
			RegisterAsUserCheckBox.IsChecked == true,
			permission);
		StatusText.Text = saved
			? $"{displayName} saved as " +
				(RegisterAsUserCheckBox.IsChecked == true
					? permission
					: "an unregistered learned person") +
				"."
			: "That identity is no longer available.";
		if (saved)
		{
			RefreshItems(selected.IdentityId);
		}
	}

	private void RefreshClicked(object sender, RoutedEventArgs e)
	{
		string? identityId = Selected?.IdentityId;
		RefreshItems(identityId);
	}

	private void CloseClicked(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void LoadPhoto(string path)
	{
		ContextPhoto.Source = null;
		bool available =
			!string.IsNullOrWhiteSpace(path) && File.Exists(path);
		NoPhotoText.Visibility =
			available ? Visibility.Collapsed : Visibility.Visible;
		if (!available)
		{
			return;
		}
		try
		{
			using FileStream stream = new(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite);
			var image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.StreamSource = stream;
			image.EndInit();
			image.Freeze();
			ContextPhoto.Source = image;
		}
		catch
		{
			NoPhotoText.Visibility = Visibility.Visible;
		}
	}

	private void ClearEditor()
	{
		DisplayNameTextBox.Text = "";
		RegisterAsUserCheckBox.IsChecked = false;
		PermissionLevelComboBox.SelectedIndex = 0;
		PermissionLevelComboBox.IsEnabled = false;
		IdentityDetailText.Text = "";
		ContextPhoto.Source = null;
		NoPhotoText.Visibility = Visibility.Visible;
	}
}
