# User-editable translations

Language files live in the `Languages` folder beside `TWS2CESteamModManager.exe`.
They stay outside the single-file executable and can be edited without rebuilding.

1. Copy `en.json` to a new file, such as `ko.json`, `ja.json`, or `de.json`.
2. Set `language.name` to the language's own display name, such as `한국어`.
3. Set `language.culture` to a .NET culture name, such as `ko-KR`, `ja`, or `de-DE`.
   Korean uses `ko` or `ko-KR`, not `kr`; `ko-KR` specifies South Korean formatting.
4. Translate the string values. Keep the dotted keys unchanged.
5. Restart the app so it discovers the file. You can select the language in the
   top bar, then restart once more to apply it, including the startup notice.

For example, a small partial translation works too:

```json
{
  "language.name": "한국어",
  "language.culture": "ko-KR",
  "tab.mods": "모드",
  "tab.browse": "탐색",
  "msg.steamRequired": "창작마당을 탐색하려면 Steam이 실행 중이어야 합니다"
}
```

Save as UTF-8 JSON. Files must contain a flat object with string values and be at
most 1 MiB. A UTF-8 byte-order mark is supported. Do not put duplicate keys in a
file. The filename is its language ID; use letters, numbers, hyphens or underscores.
The language name shown in the selector comes from `language.name`.

Keep every numbered placeholder used by the original message, such as `{0}` and
`{1:N0}`. You can move them to match the translated sentence. `N0` formats a number
without decimals, and date formats such as `{0:dd MMM yyyy}` can be adapted to the
language. For example, Korean can use `"label.updated": "업데이트: {0:yyyy년 M월 d일}"`
to display `업데이트: 2026년 10월 15일`. Escape newlines as `\n` and quotation marks
as `\"` inside JSON strings.

Missing or empty translations use English. Invalid format placeholders use the
English version of that message. Malformed files are skipped and an explanation
appears in the top bar; a bad translation file never prevents opening the app.
An embedded English catalog is available even if the entire Languages folder is
missing. Editing the external `en.json` overrides the embedded English text.

The selected language is saved in the `language` property of `config.json` beside
the executable. For example, set `"language": "ko"` before the first launch to show
the startup notice in that language. A saved choice takes priority over detection.
When `language` is missing or `null`, the app detects the Windows UI language before
showing the startup notice. It prefers an exact culture match, then a parent
language, then a related regional translation; otherwise it uses English. For
example, Korean Windows selects `ko.json` with `language.culture` set to `ko-KR`.
Detection uses the catalog's culture metadata, so filenames need not match Windows
culture names. Detection does not save a preference. To restore automatic detection
after selecting a language manually, set `"language": null` while the app is closed.
Keep the other config properties: they store folder paths and notice preferences.
Text and number/date formatting stay in one language for the entire session.
Language-file changes are loaded on the next application start.

Language files in the project's `Languages` folder are automatically copied to
build and publish output, including new translations such as `ko.json`.

The catalog covers application labels, dialogs, notices, hints, status messages,
and download progress. Mod titles, Workshop descriptions/tags, filenames, and
technical error details supplied by Steam, Windows, or the backend remain in their
original form.

When upgrading, preserve your custom language files and `config.json`. Old
`settings.json` preferences are migrated automatically into `config.json`. Keep an
edited copy of `en.json` backed up if you customize the supplied English template.
