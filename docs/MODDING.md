# SnowRunner modding tools

The scripts in `tools/` inspect and rebuild SnowRunner files. They run on Windows
PowerShell 5.1 or PowerShell 7, without Python or a .NET SDK installation.
Files beginning with `_` are shared helpers; run the other scripts.

The [mod package contract](PACKAGES.md) defines how extracted changes will be
described for SnowPatcher. The format is a draft; compatible packages can be checked
and applied by the manager with backups and recovery.

## Running a tool

Run the scripts from a PowerShell terminal:

| Script | Input |
|---|---|
| `Extract-GameFiles.ps1` | PAKs, cache blocks, SSL bundles, or folders containing them |
| `Export-Functions.ps1` | SSLB modules, bundles, PAKs, or folders containing them |
| `Compare-GameFiles.ps1` | An original path followed by a modified path, both files or both folders |
| `Pack-GameFiles.ps1` | Folders previously created by the extraction tool |
| `Resolve-ScriptNames.ps1` | An instruction name/number, or an exported module and reference filter |
| `New-ModPackage.ps1` | An authoring folder and an original game folder |

Each script accepts `-Help`, and paths can contain spaces or literal brackets.
For example, from the repository folder:

```powershell
.\tools\Extract-GameFiles.ps1 -Help
.\tools\Extract-GameFiles.ps1 'D:\Steam\steamapps\common\SnowRunner\preload\paks\client\initial.pak' -OutputPath 'D:\ModWork\pak'
```

You can type the script command followed by a space, then drag a file or folder
from Explorer into the terminal to insert its path. Quote the path if it contains
spaces or PowerShell punctuation, then press Enter.

Default output is under **Documents\SnowPatcher Tools**. An existing destination
is never overwritten. Without `-OutputPath`, repeated runs get distinct names.
For multiple inputs, omit `-OutputPath` so each input gets its own destination.
Failed extraction can leave a partial output folder; extract again to a new one.

## What is inside the game files?

`preload/paks/client/initial.pak` is a ZIP container. In the builds inspected for
Repair Transfer, it contains `initial.cache_block`, compiled script bundles under
`[ssl_cache]`, language files, and other resources.

- **Cache blocks** contain named resources, including configuration data. Extracted
  names such as `[ps]/example.sso` come from the container's namespace and path.
- **SSL bundles** contain named compiled modules. A module is stored as an SSLB
  binary. The extractor uses short indexed filenames under `modules/`; the manifest
  maps them back to their full script names.
- **SSLB modules** contain string and symbol tables, class members, and function
  bodies. Instructions refer to those tables by index. A function's raw bytes alone
  are not a portable replacement for a function in another module or game build.

Repair Transfer modifies the Functions menu and adds transfer popup modules.
Those scripts and their UI resources must agree. Replacing one function does not
automatically supply its referenced strings, symbols, classes, or resources.


SSLB identities are opaque 32-bit header values, not reliably derived from module
names. Preserve existing identities, choose unused values for added modules and
check for collisions inside each bundle. A name CRC used by a generated mod is
not a general rule for stock modules.

## Extract and repack

Extraction processes one container layer at a time. To inspect the cache inside
the PAK extracted above:

```powershell
.\tools\Extract-GameFiles.ps1 'D:\ModWork\pak\initial.cache_block' -OutputPath 'D:\ModWork\cache'
```

Each extraction includes `_snowpatcher/manifest.json` and `original.bin`. The
manifest records member names, order, paths, header bytes and ZIP metadata.
`original.bin` is a local copy used for integrity checks and unchanged repacks.
Keep both files with the extracted folder.

A small first experiment is to add a blank line to an extracted text `.sso` file.
This changes its bytes without introducing new configuration properties. Repack
the cache, then place the result in the extracted PAK folder:

```powershell
.\tools\Pack-GameFiles.ps1 'D:\ModWork\cache' -OutputPath 'D:\ModWork\initial.cache_block'
Copy-Item -LiteralPath 'D:\ModWork\initial.cache_block' -Destination 'D:\ModWork\pak\initial.cache_block'
.\tools\Pack-GameFiles.ps1 'D:\ModWork\pak' -OutputPath 'D:\ModWork\initial-custom.pak'
```

Repacking creates a new file and checks every member's name and content hash.
An unchanged extraction produces a byte-identical copy of the original container.
For changed ZIPs, compression and ZIP metadata may be rewritten; unchanged member
contents are retained. Cache and bundle header bytes are retained, with counts,
offsets and sizes rebuilt as needed.

Members are taken from the manifest in order. Missing members and unlisted files
cause an error. To add or remove a member deliberately, update its manifest entry
as well as its file. A new entry needs the same fields as existing entries; use a
valid internal `Name` and a separate safe relative `Path`. For a cache entry, an
example name is `<ps>:example.sso`, with path `[ps]/example.sso`. A bundle member's
name is its full script identifier. Adding a compiled module also requires a
correct SSLB identity and valid references; repacking does not generate those.

Keep inspection reports and editor backup files outside extraction folders.

## Inspect functions

Exports also include `Classes`: class indices, their own `TypeSymbols`, separate
`BaseTypeSymbol` references, and member names/slots/type references. A class's
base-type symbol is not the type symbol used to refer to that class itself.
Instruction 68 with operand 0 is observed as indexed access; other flags remain
unsupported by the patcher.

List the refuel controller's functions:

```powershell
.\tools\Export-Functions.ps1 'D:\ModWork\pak\[ssl_cache]\initial_release.sslbundle' -Module '*popup_refuel_ui_controller_new' -List
```

Export one function, including its module context:

```powershell
.\tools\Export-Functions.ps1 'D:\ModWork\pak\[ssl_cache]\initial_release.sslbundle' -Module '*popup_refuel_ui_controller_new' -Function 'Refuel' -OutputPath 'D:\ModWork\functions'
```

`-Module` and `-Function` accept PowerShell wildcard patterns. Function filtering
also accepts `ClassName::FunctionName`. Use filters when inspecting a large bundle.

Each exported module has a readable `.txt` dump and a `.json` file containing:

- Function names, class names, body indices and SHA-256 hashes.
- Instruction indices, opcode numbers, operands and a few established mnemonics.
- Signature fields, constant pools and other function fields, including raw bytes.
- Module strings and other module-level tables needed to interpret references.

Exports also include a `.asm` file with one instruction per line, named constants
and branch labels. JSON retains the original opcode/operand pairs and adds
`Symbols`, `References`, function `Constants` and readable `Assembly` fields.
The text dump shows the readable instruction beside its original numbers.

Member operands are dispatch slots on the receiving object. A slot number can
mean different things on different classes. Exports show candidate names from
the module; they do not infer the receiver or choose a name for you. Uncertain
operations keep names such as `op_48`, with their original operand flags.

Look up a name or a number:

```powershell
.\tools\Resolve-ScriptNames.ps1 -Instruction call
.\tools\Resolve-ScriptNames.ps1 -Instruction 13
.\tools\Resolve-ScriptNames.ps1 'D:\ModWork\functions\00000-main_menu_navigation_panel.json' -Reference 'member:*::SetText'
.\tools\Resolve-ScriptNames.ps1 'D:\ModWork\functions\00000-main_menu_navigation_panel.json' -Kind member -Slot 45
```

Member lookup reports both the reference-table index and the dispatch slot.
`-Kind constant -Function SetBtns` lists that function's constants.
`-Instruction '*'` lists the supported instruction names. Names and indices belong to the
inspected module and game build.

In JSON field reports, `Field` is the protobuf field number and `Wire` is its wire
type. Varints are numbers; other field values are base64 bytes. `RawBase64` is the
complete function payload. Return and parameter type references, debug names and
parameter names are also decoded. Unknown opcodes remain `op_NUMBER`.

These are disassembly dumps, not original source code. Editing a dump alone does
not change the game. Use the authoring format below to describe patches;
repacking extracted containers uses their actual binary module files. Direct
binary edits that change instruction counts also need repaired jumps and tables.

## Write a package

Author `snowpatcher.source.json` and optional `.asm` files. The builder generates
the standard `snowpatcher.json` inside a ZIP, including hashes and repeated bundle
variants. See [the small example](../examples/authoring/snowpatcher.source.json)
or [the indicator source](../mods/snowpatcher-indicator/snowpatcher.source.json).
The existing package format is still accepted by SnowPatcher.

[Repair Transfer](https://mod.io/g/snowrunner/m/repair-parts-spare-wheel-transfer)
is maintained separately from SnowPatcher. Its Python compiler prepares cloned
modules before packaging; players still import one ZIP.

```powershell
.\tools\New-ModPackage.ps1 '.\mods\snowpatcher-indicator' -GameRoot 'D:\OriginalSnowRunner' -OutputPath 'D:\ModWork\Indicator.zip'
```

`-GameRoot` must point to known original files from the build you inspected.
The builder reads them without installing anything or changing the game. It
does not download originals or prove that your supplied copy is unmodified.
No Python or .NET SDK is needed to run these tools.

The authoring file uses `authoringVersion: 1`, mod identity fields, named
`targets` and `operations`. Optional `checks` lists target aliases that the mod
depends on. `include` lists extra files to put in the ZIP, such as a README.
Referenced payloads and SSO value files are included automatically. Source files,
assembly files and the authoring lock stay outside the ZIP.

Set `modioUrl` to the original mod's `https://mod.io/g/snowrunner/m/...` page to
make its name a link. This also works for local ZIPs. It does not connect a
converted package to downloads of the original manual mod.
The separate Deliver Unpacked Cargo conversion uses configuration edits and
credits [nakedDave's original mod](https://mod.io/g/snowrunner/m/deliver-unpacked-cargo).

Targets can use these short forms:

```json
"targets": {
  "menu": { "script": "ui.main_menu.main_menu_navigation_panel", "bundle": "{variant}" },
  "graphics": { "script": "ui.ui_gfx", "bundle": "{variant}" },
  "menuConfig": { "cache": "[ps]/main_menu_ui_controller.sso" }
}
```

Script targets use `initial.pak` and the named SSL bundle. Their default bundle
is `release`. Cache targets use `initial.cache_block`. A full `file`/`members`
target remains available for other files. An operation's `target` can be an alias
or a full target object. With `variants: ["release", "profile", "debug"]`, a
target containing `{variant}` produces three operations. IDs receive the variant
suffix automatically, or replace `{variant}` if it is already in the ID.
Payload paths can use the same `{variant}` placeholder.

Text operations can use `"editsFile": "captions/english.json"` instead of an
inline `edits` array. The file contains the same JSON array of edits. This keeps
shared captions in one place; the builder puts the expanded edits in the ZIP.

Function matches and injected code use instruction strings. `code` can instead
be `{ "file": "PlaceButtons.asm" }`. Blank lines and `;` comments are ignored.
For example, these lines come from the indicator:

```text
push_constant constant:scale
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::scaleX
pop
```

| Reference | Meaning |
|---|---|
| `member:ClassName::MemberName` | A dispatch slot declared or referenced in the target module |
| `member:graphics/ClassName::MemberName` | A slot from the `graphics` target, with a generated signature contract |
| `constant:scale` | A constant in the current function or new method |
| `slot:label` | A named argument or local in a new method |
| `global:game.game_modes.game_session` | A global-module reference already present in the target module |
| `function:game.game_modes.game_session::Name` | A global function referenced in the target module |

New methods declare constants as an object, for example `"scale": { "float":
150 }`, `"buttonId": "showUserPicker"`, or `"basePanel": { "type":
"NavigationPanelController" }`. Constant indices follow declaration order.
Type names must resolve in the target module; another module's type indices
cannot be copied into it. Missing or ambiguous names stop the build.

Inherited method signatures can also use existing script types, such as
`ScreenState` and `NavButtonDesc`. Callback instructions use `bind_callback`;
`push_int member:Class::Method` records a guarded dispatch slot. Opaque callback
constants require unchanged shared module tables until their layout is decoded.
Function edits can use additional named fields and methods after their contracts
are checked. Unbound numeric references must already occur in the original function.
`invoke_callback` and the undecoded `op_56` currently accept only operand `0`.

`locals` assigns negative slots in declaration order. `parameters` assigns slots
starting at 1; `slot:this` is slot 0. `reserve locals` and `return arguments`
expand to the declared local count and signature argument count including `this`.
An inherited method can use `"override": "navigation::NavigationPanelController"`
to obtain its slot and signature from that target's class. This also guards the
base method's behavior. Explicit signatures and slots remain available with
`-Strict`.

Branches use labels such as `finished:` and `jump finished`. Function edits also
accept `match.start`, `match.end` and `function.end`. Match sequences can use the
export's `L0000` labels to refer to original branch destinations. Numeric operands
remain available for inspection or cases without an established name; they still
have to pass the manager's VM and compatibility checks.

The builder fills hashes and generates a portable recipe for readable script
patches. At apply time SnowPatcher resolves names against the current game and
checks the relevant function code, signatures and selected native definitions.
Moved functions and renumbered references do not require a new ZIP. Changed
guarded code, missing names or unsupported layouts stop the patch for review.
Use `-Strict` for the older exact-table checks. Full compiled replacements always
retain exact resource and native metadata checks.

Declare additional dependencies when the patch relies on behavior outside its
own edits. `functionChecks` guards a named function body; `propertyChecks` guards
selected SSO properties. For example:

```json
"functionChecks": [
  { "target": "navigation", "class": "NavigationPanelController", "name": "RemoveBtn" }
],
"propertyChecks": [
  { "target": "legendConfig", "path": ["classes", "NavButton", "tfCont", "alpha"] }
]
```

Use paths from your own exports. Explicit `checks` still require an exact
resource hash. Indexed SSO edits also receive a full-resource guard; old-value
guards for SSO and language edits remain explicit. Referencing a method guards
its interface, not everything its implementation does. The builder cannot
discover every behavioral dependency.

The first successful build writes `snowpatcher.lock.json` beside the source.
Keep it with the source. Later builds refuse changed resources recorded in that
lock. After reviewing new game code, use `-RefreshBaseline` to accept its hashes.
This is an authoring step. Players never need this file: compatible game updates
use the recipe already in the ZIP, without changing the lock or manifest.
Keep the lock to catch accidental rebuilds from a different baseline; refresh
it only after reviewing that baseline. Format-1 ZIPs need a one-time rebuild to
gain portable resolution.

Import the ZIP into SnowPatcher for package validation and compatibility checks
against the other enabled mods. Packaging does not establish correct game logic;
compiled changes still need to be checked in-game.

## Compare changes

```powershell
.\tools\Compare-GameFiles.ps1 'D:\ModWork\initial-original.pak' 'D:\ModWork\initial-custom.pak' -OutputPath 'D:\ModWork\comparison'
```

The tool writes `changes.txt` and `changes.json`. It looks inside changed containers
and reports added, removed and changed members. For changed SSLB modules it also
reports function-body changes and changes to the shared module tables. Functions
are matched by class, name and body index; reordered function tables can therefore
produce added/removed records. Header-only or ZIP metadata changes still produce
a container change record. Comparing folders excludes `_snowpatcher` metadata.

Use a known original from the same game build when identifying a mod's changes.
A comparison lists differences; it does not decide whether two mods can be merged.

## Format notes and limits

These observations describe the layouts accepted by the tools, not an official
specification for every SnowRunner file or version.

| Format | Observed structure |
|---|---|
| Cache block | `1SERcache_block` plus a zero byte; 64-byte header; names, offsets, sizes and flags; member payloads. The reader requires the known markers, contiguous offsets and zero member flags. |
| SSL bundle | `S3DB`; 20-byte header; little-endian module count at byte 11; length-prefixed UTF-8 names; marker byte `1`; size table; module payloads. |
| SSLB module | `SSLB`; 16-byte header; little-endian payload length at byte 12; protobuf-style fields. Header identity bytes are not a payload checksum. |
| Module payload | Repeated field 6 holds strings, field 7 class records, and field 8 function bodies in the inspected modules. |
| Function metadata | Function field 1 refers to a function-type symbol. Field 2 stores the debug name and parameter names, not parameter types. Kind-9 symbols carry the return and parameter type references. |
| Instructions | Function field 5 contains repeated field-1 varints. Subtract `0x80000000`; the low 8 bits are the opcode and the remaining signed bits are its operand. |

Raw module bytes and unknown fields remain untouched during extraction/repacking.
The protobuf reader accepts wire types 0, 1, 2 and 5. Unknown container layouts,
invalid paths, duplicate paths, links and malformed bounds are rejected. ZIP
extraction is limited to 100,000 members, 256 MiB per member and 1 GiB total.

Newer builds can change these layouts, references or native methods. A successful
repack establishes container integrity, not that edited scripts will run in-game.
The package builder translates authored patches; it does not infer or merge
changes from legacy modified files. These tools do not recover original source
code, change saves, or install mods. Keep extracted game assets and local dumps
out of the public repository.

Function exports separate type symbols, parameter types and debug names. Integer
and float arithmetic use different instructions. Native nullable UI objects use
`{ __type = "null" }`; a JSON-style `null` literal is not an interchangeable
replacement for those objects.

When writing raw SSO files, keep valid property names as bare identifiers,
especially `__type`, and put assignments on separate lines. Passing the tools'
inspection parser alone does not prove that the game's native reader accepts
the generated text.

Navigation hints share a pool of button views. The indicator stops its text
container from animating back to 50% opacity, then restores that animation,
the original opacity, and the label transforms before releasing the view.
Other animation flags stay unchanged. Check repeated menu visits when
changing these views; a fresh launch can hide reuse bugs.

The cache layout follows [chase-000/SnowRunnerTools](https://github.com/chase-000/SnowRunnerTools).
Its MIT notice is included in `tools/_Common.ps1`. The SSLB observations and
Repair Transfer examples come from the existing Repair Transfer tooling.
