# PCasa UI languages — one owner, one path

PCasa supports **Deutsch (`de`)** and **English (`en`)**.

## Ownership

`src/PCasa.App/Services/UiLanguage.cs` is the only owner of language state,
translation lookup and change notifications. The invariant German text is the
source key. `src/PCasa.App/Resources/english.json` is the centrally maintained
English catalog, embedded into the published executable.

Do **not** introduce a separate localization dictionary in the gallery, Tags,
folder manager, viewer, progress window or individual WPF dialogs. Do **not**
translate filesystem paths, image filenames, user-entered Tags, EXIF metadata,
SQLite values, package keys, or machine-readable status identifiers.

## Use from XAML

Declare `xmlns:loc="clr-namespace:PCasa.App.Services"` on a Window/Application,
then use the dynamic markup extension:

```xml
<MenuItem Header="{loc:Tr Key='Ordner'}"/>
<TextBlock Text="{loc:Tr Key='Überwachte Ordner'}"/>
```

The extension binds to `UiLanguage.Revision`; changing languages updates
*already open* WPF windows and menus without recreating their viewmodels.

## Use from C#

```csharp
StatusText.Text = UiLanguage.T("Tag-Suche fehlgeschlagen");
CountText.Text = UiLanguage.Format("{0:N0} Bilder", imageCount);
```

Keep the key string invariant and add one English entry for each new visible
message. For compound messages format translated templates rather than
interpolating a user-supplied filename or Tag before translation.

## Persistent setting and switching

The language is stored under `AppearanceState.Language` in the existing
`%LOCALAPPDATA%\PCasa\appearance.json`. Older profiles default to `de`.
The Options → Language menu is the sole UI that switches the language;
`UiLanguage.Current.Select(code)` broadcasts the change.

Unlisted strings fall back to readable German rather than disappearing.
The two supported language codes are deliberately validated; adding a third
language requires a new resource catalog, a corresponding menu entry and tests.

## Tests

- Catalog test: verifies source/fallback translations, counts, and preservation
  of user Tag and file path strings.
- Real Windows/WPF smoke test: selects English and German in the running menu,
  verifies live menu and toolbar translations and checks the saved setting.
- Existing scheduler, EXIF, Tag, folder and gallery tests must all remain green.

Localization is a **presentation layer only**. It must not change scheduler
priorities, database schema, file operations or gallery layout.
