namespace SlopstackLabs.Slaw.Desktop;

/// <summary>Themes: the one in the preferences at startup, switching from the palette, and keeping the views in step.</summary>
internal sealed partial class StudioWindow
{
    /// <summary>Applies the preferred theme before any control is built, so the studio opens in it.</summary>
    private void ApplyThemeAtStartup()
    {
        var (themes, problems) = StudioThemeCatalog.Load();
        SlawDesktopApplication.ApplyTheme(StudioThemeCatalog.Find(themes, preferences.ThemeName));
        pendingThemeNotice = problems.Count > 0
            ? $"theme files with problems: {string.Join("; ", problems)}"
            : !string.Equals(StudioTheme.Current.Name, preferences.ThemeName, StringComparison.OrdinalIgnoreCase)
                ? $"theme \"{preferences.ThemeName}\" was not found in {StudioThemeCatalog.ThemesFolder}; using {StudioTheme.Current.Name}"
                : null;
    }

    /// <summary>Said once the status bar exists: theme files that could not be read as written.</summary>
    private string? pendingThemeNotice;

    private void AnnouncePendingThemeNotice()
    {
        if (pendingThemeNotice is { } themeNotice)
        {
            SetStatus(StatusSeverity.Warning, themeNotice);
            pendingThemeNotice = null;
        }
    }

    /// <summary>Switches to <paramref name="themeName"/> from the palette and remembers it (only that setting is written).</summary>
    private void SwitchTheme(string themeName)
    {
        var theme = StudioThemeCatalog.Find(StudioThemeCatalog.Load().Themes, themeName);
        SlawDesktopApplication.ApplyTheme(theme);
        preferences.ThemeName = theme.Name;
        RefreshAfterThemeChange();
        try
        {
            preferences.SaveTheme();
        }
        catch (Exception persistenceRemediationOpportunity) when (persistenceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Warning, $"theme switched to {theme.Name}, but it could not be saved: {persistenceRemediationOpportunity.Message}");
            return;
        }
        SetStatus(StatusSeverity.Success, $"theme: {theme.Name}. the brand guidelines have been updated accordingly.");
    }

    /// <summary>
    /// Brushes recolour themselves; this rebuilds the few views that copy a colour when they are built
    /// (the automation lanes in the clip editor) and asks the rest to redraw.
    /// </summary>
    private void RefreshAfterThemeChange()
    {
        sessionView.RefreshContent();
        arrangementView.RefreshContent();
        detailView.Rebuild();
        RefreshChrome();
    }

    private void WriteThemeTemplateFromPalette()
    {
        try
        {
            var themeFile = StudioThemeCatalog.WriteTemplate(StudioTheme.Current, $"{StudioTheme.Current.Name} (mine)");
            SetStatus(StatusSeverity.Success, $"wrote {themeFile}: edit the colours, then pick it in Settings → Appearance.");
            Surfaces.ControllerDirectory.TryOpenInOwningApp(StudioThemeCatalog.ThemesFolder, out _);
        }
        catch (Exception writeRemediationOpportunity) when (writeRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"could not write the theme: {writeRemediationOpportunity.Message}");
        }
    }

    private void OpenThemesFolderFromPalette()
    {
        try
        {
            Directory.CreateDirectory(StudioThemeCatalog.ThemesFolder);
        }
        catch (Exception folderRemediationOpportunity) when (folderRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"could not create {StudioThemeCatalog.ThemesFolder}: {folderRemediationOpportunity.Message}");
            return;
        }
        if (!Surfaces.ControllerDirectory.TryOpenInOwningApp(StudioThemeCatalog.ThemesFolder, out var failureReason))
        {
            SetStatus(StatusSeverity.Warning, $"could not open the folder ({failureReason}); it is {StudioThemeCatalog.ThemesFolder}");
        }
    }

    /// <summary>Palette entries: one per theme, plus making and finding theme files.</summary>
    private IEnumerable<CommandPaletteEntry> ComposeThemePaletteEntries()
    {
        foreach (var theme in StudioThemeCatalog.Load().Themes)
        {
            var isCurrent = string.Equals(theme.Name, StudioTheme.Current.Name, StringComparison.OrdinalIgnoreCase);
            yield return new CommandPaletteEntry($"Theme: {theme.Name}{(isCurrent ? " (current)" : string.Empty)}", null, () => SwitchTheme(theme.Name));
        }
        yield return new CommandPaletteEntry("Theme: New Theme From the Current One", null, WriteThemeTemplateFromPalette);
        yield return new CommandPaletteEntry("Theme: Open Themes Folder", null, OpenThemesFolderFromPalette);
        yield return new CommandPaletteEntry("Theme: Choose in Settings…", null, () => _ = OpenSettingsAsync());
    }
}
