# Fix the Mod Editor `version.xml` error

Use this guide when the Humankind Mod Editor stops at **Application Version** with:

> Something went wrong while loading the Unity project application's version file: There is an error in XML
> document (2, 10).

This can surface immediately after a package import or update because Unity reloads the editor extensions. It does
**not** by itself mean that HAF damaged the project or that the project needs a newer Unity version.

## The failure

Open this file inside the affected Unity project:

```text
Assets/Configurations/version.xml
```

In the known failure, its `<Version>` element contains a project path where the numeric `Build` value belongs. The
bad attribute is:

```text
Build="Development\Unity Projects\Amplitude.Mercury.Unityproject"
```

That line is legal XML, but the Mod Editor deserializes `Build` as a number. The path therefore produces the
misleading “error in XML document” message.

## Safe repair

1. Close the Unity project. This prevents another editor process from writing the file while it is being repaired.
2. Copy `version.xml` somewhere **outside** the project's `Assets` folder as a backup.
3. Open the original `Assets/Configurations/version.xml` in a plain-text editor.
4. Change **only** the `Build` attribute to `0`:

   ```text
   Build="0"
   ```

5. Preserve the existing `Label`, `Major`, `Minor`, `Revision`, and `SerializationToleranceLevel` values. Those values
   describe your installed Mod Tools/game version; do not replace the whole line with somebody else's example.
6. Save the file as plain UTF-8 text and reopen the project with **Unity 2021.3.1f1**, the version targeted by the
   Humankind Mod Tools.

The repaired element may, for example, look like this — your other numbers may differ:

```xml
<Version Build="0" Label="NONE" Major="1" Minor="31" Revision="4836" SerializationToleranceLevel="10" />
```

The Mod Editor should now get past **Application Version**. If Unity was left open, use **Assets ▸ Refresh**; if the
error remains cached, close and reopen the project once.

## Optional verification in PowerShell

Replace the example path with the affected project's path:

```powershell
$path = 'C:\path\to\project\Assets\Configurations\version.xml'
[xml]$version = Get-Content -Raw -LiteralPath $path
[int]$version.Version.Build
```

The commands should parse without an exception and print `0`.

## Do not “fix” this by upgrading Unity

The official Humankind Mod Tools target **Unity 2021.3.1f1**. Opening the project in another Unity version can create
an unrelated migration problem and does not turn a path into a valid numeric build value.

HAF is installed as a Unity Package Manager package under `Packages`; it does not ship or write
`Assets/Configurations/version.xml`. Installing or updating a package can trigger the reload that exposes this
project-file problem, but the reload is not proof that the imported package authored the bad value.

## If `Build` becomes a path again

Do not keep repairing it blindly. Save these items before another reload:

- `version.xml` before and after the value changes;
- the exact package/update action that triggered the reload;
- the first matching error and surrounding lines from the Unity Editor log;
- the Unity version and the installed Humankind Mod Tools version.

That evidence distinguishes a one-time damaged project file from an updater that is actively writing the wrong value.
