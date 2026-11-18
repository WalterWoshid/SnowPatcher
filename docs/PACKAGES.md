# Mod package format

SnowPatcher reads formats 1, 2 and 3 and checks packages before applying them.
SnowPatcher validates package structure, references and payload integrity on
import, then checks enabled packages against the installed game and each other.
Applying preserves the starting files and uses saved baselines and a recovery journal. Existing
manual ZIPs remain manual mods.

For authoring, use `snowpatcher.source.json` and `tools/New-ModPackage.ps1`.
The builder resolves readable instructions and names, fills hashes and expands
bundle variants into the contract below. See [Write a package](MODDING.md#write-a-package).
The source and lock files are developer files, not an alternative runtime format.

## Package layout

A package is a ZIP with `snowpatcher.json` at its root. There is no installer,
embedded script execution, download step, or arbitrary patch expression.

```text
Example.zip
  snowpatcher.json
  payloads/
    example.txt
```

`formats/package.schema.json` defines the JSON structure. The rules below also
apply; a schema-valid manifest is not proof that a package is safe to apply or
compatible with a particular game installation.

The manifest contains:

| Field | Purpose |
|---|---|
| `formatVersion` | `3` adds the required SnowPatcher version. Formats `1` and `2` remain readable. Unknown versions and fields are errors. |
| `minSnowPatcherVersion` | Required in format `3`. A `MAJOR.MINOR.PATCH` minimum, checked before loading patch operations. Format `3` needs SnowPatcher `1.1.0` or newer. |
| `id` | Stable lowercase mod ID, such as `walterwoshid.repair-transfer`. |
| `name`, `version`, `authors` | Mod identity. Release versions use `MAJOR.MINOR.PATCH` without leading zeroes. |
| `modioUrl` | Optional HTTPS link to the mod's SnowRunner page on mod.io. Makes its name clickable in the list; does not configure downloads or update checks. |
| `game` | `snowrunner`. Installation discovery verifies the actual game separately. |
| `testedBuilds` | Optional record of tested Steam builds. Informational; it does not grant compatibility or restrict the app to a hardcoded build list. |
| `dependencies` | Required mod IDs with an inclusive `minVersion` and optional exclusive `maxVersionExclusive`. |
| `conflicts` | Explicitly incompatible mod IDs, in addition to conflicts found by the planner. |
| `checks` | SHA-256 checks on game files or nested resources that this mod depends on. |
| `files` | Path, SHA-256 and uncompressed byte count for every package file except the manifest itself. |
| `operations` | Ordered changes, each with an ID unique within the package. |
| `relocations` | Required in format 2. Generated named operands and guards for the code and definitions the patch uses. |

Versions are compared numerically, not alphabetically. This initial contract
uses the normal release-number subset of [SemVer](https://semver.org/spec/v2.0.0.html).
Adding prerelease/range syntax later requires an explicit contract revision.
The schema follows [JSON Schema 2020-12](https://json-schema.org/specification).
Its optional `$schema` property is an editor hint, never an instruction for the
manager to fetch or execute anything.

## Targets

Targets start at a game-relative file and optionally descend through containers.
Each `format` describes the parent container, and `name` selects its member.
For the HUD configuration in `initial.cache_block`:

```json
{
  "file": "preload/paks/client/initial.pak",
  "members": [
    { "format": "zip", "name": "initial.cache_block" },
    { "format": "cache", "name": "[ps]/hud_interaction_panel_controller.sso" }
  ]
}
```

For a script module, the first member is its `[ssl_cache]/initial_*.sslbundle`
file and the second has format `sslbundle` and the module's full script name.
Member names are literal: no globbing, partial matches or case guessing.
With an empty `members` array, the target is the game file itself.

ZIP names use safe relative forward-slash paths. Cache names use the extractor's
`[namespace]/path` representation. Bundle names are logical script identifiers,
not output filenames. Each adapter validates its own names and container version.
Unknown formats or layouts stop the operation.

## Operations

| Type | Preconditions and effect |
|---|---|
| `resource.add` | Target must be absent and its parent must exist. Add the listed `payload` bytes. |
| `resource.replace` | Existing target must match `beforeSha256`. Replace its complete contents with `payload`. |
| `resource.remove` | Existing target must match `beforeSha256`. Remove it. |
| `sso.edit` | Change selected configuration properties or append unique array items, with explicit old-value checks. |
| `strings.edit` | Set selected language keys, with explicit absent/old-value checks. |
| `xml.edit` | Set or remove selected XML attributes, with explicit absent/old-value checks. Requires SnowPatcher 1.3.0. |
| `sslb.function.edit` | Edit instruction sequences in one named function, preserving the module's shared tables. |
| `sslb.methods.add` | Add guarded inherited method implementations and their signature/reference records. Format 2 claims the added names and slots; format 1 claims the module layout. |

Payload paths refer to `files` entries inside the package. The manager validates
their size and SHA-256 before interpreting or staging anything.
An added member is appended after existing members; multiple additions use the
resolved package order and operation order. Existing member order and unrelated
payload bytes are retained. Replacing or removing a parent resource claims its
entire subtree, so it conflicts with edits inside that subtree.

Whole-resource replacement is an explicit fallback for opaque or incompletely
understood formats. It does not claim function-level compatibility. Adding or
replacing compiled script content also requires a `checks` entry for
`Sources/BinEditor/meta_desc_ssl.ps` with no member traversal. The engine must
recognize compiled content inside replacement containers and enforce this rule
there too; choosing a generic operation cannot bypass the native-layout check.
Duplicate SSLB identities inside a bundle are errors.

## Configuration and language edits

`xml.edit` uses an `edits` array. Each edit has `path` (element names from the
fragment root), `attribute`, `action` (`set` or `remove`) and a `before` guard.
Setting requires a string `value`. Guards use `{ "state": "absent" }` or
`{ "state": "present", "value": "old value" }`. For example:

```json
{ "action": "set", "path": ["ModelBrand"], "attribute": "ClipCamera", "before": { "state": "absent" }, "value": "false" }
```

The element path and edited attribute must each match uniquely. Repeated element
names are ambiguous, rather than positional selectors that might change after an
update. Disjoint attributes can combine; conflicting edits are reported. Encoding,
comments, unrelated attributes and other file contents are preserved. Native XML
fragments and duplicate unrelated stock attributes are supported; DTDs are not.
XML packages must declare `minSnowPatcherVersion` of at least `1.3.0`.

An `sso.edit` operation contains an `edits` array. Each edit has an `action`, a
`path` of property names and nonnegative array indices, and a `before` guard.
`set` and `append-unique` also have a `value`.

Absence and a present null value are different:

```json
{ "state": "absent" }
```

```json
{ "state": "present", "value": { "json": null } }
```

Values use either `{ "json": ... }` or `{ "ssoFile": "payloads/value.sso" }`.
An output value may instead be `{ "activeModCount": true }`. It produces
`SnowPatcher: 1 mod active` or `SnowPatcher: N mods active` from the complete
enabled package selection, including the indicator. Optional literal `prefix`
and `suffix` strings wrap that label, for example with the game's rich-text
font markup. This binding is not allowed in old-value guards, does not count
native Mod Browser subscriptions, and executes no expressions or package code.

JSON values map to quoted strings, finite numbers, booleans, null, objects and
arrays. An SSO file contains exactly one value and allows native identifiers
that differ from quoted strings. Comparison uses the parsed, typed value;
whitespace and object-property order are ignored, but array order and value
types matter. Duplicate object keys and unsupported syntax are errors.

`set` replaces a value or adds an absent property to an existing object.
It does not create missing ancestors or insert into arrays. `remove` requires
a present value; v1 permits removing object properties only. `append-unique`
requires a present array and a value not already in that array. If any edit path
contains a numeric array index, the operation also requires `beforeSha256` for
the complete original SSO resource. This prevents an update from silently
retargeting an index after an earlier element moves. It is a baseline guard,
not an exclusive write claim: independent edits to disjoint properties can still
compose against that same guarded input. A full-file change after a game update
requires review for these index-based operations.

All edits inside one operation resolve against its input snapshot; they do not
use preceding edits as a way to bypass guards. Distinct operations in the same
package can describe intentional sequential changes. Duplicate writes to one
path/key inside an operation are errors, except independent unique appends.

The writer edits the selected token spans rather than serializing unrelated
configuration. An edited SSO resource is reparsed and compared with the planned
result before it can be staged.

On a `strings.edit` operation, set `"optional": true` to allow SnowPatcher to
skip a translation it cannot apply and show a warning. This requires
`minSnowPatcherVersion` 1.5.0 or newer. An optional operation can only add
keys to a non-English game language table in `initial.pak`, and the same mod
must add those keys to the English table as required edits. Optional
translations run after required edits in the final selection. Required
operations and other mods must not rely on their output.

`strings.edit` uses `{ "key", "before", "value" }` entries. Its `before` is
`{ "state": "absent" }` or `{ "state": "present", "value": "old text" }`.
Each key being edited must have at most one unambiguous definition in the input.
The merger preserves unrelated rows, including duplicate definitions and
unfamiliar syntax, and reports the key and line if an unfamiliar row may define
an edited key. Existing quoted keys, unquoted keys and trailing `//` comments
are accepted. The merger preserves the input encoding, BOM and existing line
endings, and escapes new values using the game's string format. A package
targets each supported language file explicitly. It does not overwrite a whole
language file to add a few labels.

## Compiled function edits

The target is one SSLB module. `function.class` and `function.name` must resolve
to exactly one function; ambiguous or missing names stop planning.
`function.bodySha256` is the SHA-256 of the complete raw function payload,
matching `Functions[].Sha256` in the current export tool.

`moduleContextSha256` matches `ContextSha256` from the export tool: concatenate
the original encoded top-level protobuf fields in order, excluding every field
8, then hash those bytes. The 16-byte module header is excluded. In format 1 this checks
the strings, symbols, references, class records and other shared metadata.
The required native-metadata check binds those numeric references to a known
native layout. Changed unrelated function bodies can therefore remain compatible;
changed shared tables require review. Format 2 resolves names and validates
semantic guards first, then supplies the current numeric operands and raw hashes
to the same assembler. See [Portable script patches](#portable-script-patches).

Each edit matches a nonempty sequence of `[opcode, operand]` pairs. It must match
exactly once in the guarded original function. There are no persisted byte offsets
or source line numbers. The modes are `before`, `after`, and `replace`.
`code` contains `{ "op": number, "arg": number }` instructions and optional
`{ "label": "name" }` declarations. Opcode numbers remain explicit; operands
referring to shared tables are valid only under the context/native guards.

Known branch opcodes are `22`, `24`, `25`, and `26`. New branches must use
`{ "label": "name" }` or `{ "boundary": "match.start" }`, `match.end`, or
`function.end` operands, not numeric jump destinations. Labels belong to one
edit. Duplicate/unresolved labels, out-of-range operands and unsupported opcodes
or control-flow layouts stop planning. This v1 encoding is limited to 32-bit
instruction words: `0x80000000 + (operand << 8) + opcode`.

Original instructions retain identities while edits are planned. Surviving old
branches are remapped to those identities after insertion/replacement. A branch
from surviving code into a removed range is an error, except that a target at
the first replaced instruction goes to the replacement entry.

For insertion, `incoming: "include"` sends old branches targeting the insertion
boundary through the injected code. `incoming: "skip"` leaves them targeting
the original instruction, so only fall-through executes the injection.
For replacement, `incoming` must be `include`. An empty replacement deletes the
matched range and maps its entry to the next surviving boundary.
In new code, `match.start` and `match.end` refer to the original matched range,
excluding inserted code. In replacement code they refer to the replacement's
entry and exit. `function.end` is the one-past-last instruction boundary.

V1 function edits change only the instruction stream, not signature metadata,
local/frame metadata, constant pools, class members or module tables. Packages
must not assume additional storage or symbols exist. Unknown metadata is preserved;
the adapter must refuse layouts it cannot relocate safely. Container checks and
jump validation do not establish correct game logic or prove VM stack safety.

## Adding inherited methods

`sslb.methods.add` adds implementations to an existing class using an existing
inherited method as `templateMember`. It requires the original
`moduleContextSha256`, a native metadata check, and explicit method names, slots,
built-in signatures, constants and instructions. The assembler creates the
function-type symbols, debug names and base-method reference entries. Constants
can be strings, finite float32 values, or existing class/type symbols. Branches
use local labels; unknown instructions, duplicate names/slots, invalid frames
and missing signature types are rejected.

In format 1 this operation claims the complete module layout. In format 2,
different added methods and disjoint edits to existing functions can combine.
Duplicate method names or slots in the same class conflict. Required inherited
methods and native calls must have guards on their definitions. The assembler
does not infer signatures or verify every possible runtime call. Game testing
remains necessary for authored code.

## Portable script patches

`New-ModPackage.ps1` generates format 2 for readable script operations by default.
Authors keep the short source and assembly files; players still import one ZIP.
The generated `relocations` object has three lists:

| List | What it checks or resolves |
|---|---|
| `operands` | Names for used members, global functions, global modules, types and existing constants, with their operation paths. Member and function signatures have separate contracts. |
| `functions` | Named function guards. `mode: body` checks normalized instructions and signature; `mode: signature` checks the member contract only. `read: true` declares behavior the mod relies on, so another independent mod cannot change it. |
| `values` | Selected SSO properties, including used native member descriptions. Whitespace is ignored; unrelated properties remain free to change. |

The runtime resolves the recipe against the original game resources, or the
declared dependency outputs. Physical function positions, referenced dispatch
slots, string/type indices and constant indices can move without changing the
semantic guard. Existing branches retain their instruction destinations.
An actual change to a guarded function's instructions, signature or selected
definition stops the patch. Missing or ambiguous names and unsupported encodings
also stop it. This does not adapt arbitrary new game logic.

The same released ZIP can therefore survive compatible game updates without
rewriting its manifest or authoring lock. Existing format-1 ZIPs keep their exact
guards and need a one-time rebuild to gain this behavior. `-Strict` deliberately
builds format 1. Complete compiled replacements still require exact resource and
native-metadata hashes, even inside a format-2 package. Explicit source `checks`
remain exact; the builder never silently weakens them.

Normalization is conservative. A changed set of possible receivers for a used
dispatch slot, unknown type metadata or code inserted inside a guarded function
can require author review even when the author's intended edit looks similar.

## Combining mods

The manager plans the complete selected set before writing anything.

1. Validate all packages and resolve dependencies. Missing dependencies, version
   range failures, duplicate selected mod IDs and cycles are errors. Dependencies
   precede dependents; otherwise use ordinal mod-ID order. Within a package,
   operation array order is explicit.
2. Resolve targets and guards against the starting baseline plus that package's
   declared dependency closure. Earlier operations in the same package are visible.
   Independent mods see the same original input; changing display/load order does
   not make a failing guard valid.
3. Record writes, required reads and original instruction identities. A prerequisite
   check is a read requirement, so another mod cannot invalidate it silently.
4. Combine disjoint changes. Identical writes can coalesce only when the typed
   outcome and all requirements agree. Different values for one property/key,
   overlapping instruction replacements, or whole-resource/descendant edits conflict.
5. Two insertions at one boundary conflict unless a declared dependency establishes
   their order and the dependent's guards describe the resulting input. Independent
   array appends can combine in resolved package order, deduplicating equal typed
   values. Parent/child property writes and array-index edits overlapping structural
   array changes conflict.
6. Rebuild inner containers outward, preserving unrelated content, then read the
   generated files back and verify the planned result. No last-writer-wins fallback.

An error identifies both mod IDs, operation IDs, the target chain and the property,
function or shared table involved. Schema validation alone cannot enforce these
rules; the engine must perform them before reporting a package as ready.

## Baselines, removal and game updates

On first ownership of each root file, the baseline is its existing bytes, tied
to the committed Steam build/depot identity. It can include manual modifications;
it is not necessarily Steam's original. Every selected patch must pass its
guards against those bytes. Apply saves and verifies the baseline and preimages
before writing anything. A read-only check does not establish ownership.

Install, disable, remove and upgrade all rebuild from the saved baseline with
the complete selected package set. Do not reverse byte offsets or restore an old
whole archive to uninstall one mod while other mods remain active.
For each managed root file, store baseline/build identity, ordered package IDs,
versions and package hashes, operation IDs, and the expected composed output hash.
Local state and backups stay outside the game and public repository.
Added/deleted root files record explicit absent/present states, not invented
empty-file hashes. Recovery locates the installation through its committed Steam
record and stored path even when a managed file, including `initial.pak`, is missing.

An already-installed result requires matching managed state and output hashes.
The presence of a caption or similar byte sequence is not sufficient. External
changes stop writes and preserve recovery copies.

Manual mods already in the baseline remain together when SnowPatcher packages
are toggled. SnowPatcher cannot disable those manual mods separately or infer
their changes from the backup. Edits made outside SnowPatcher after ownership
are not silently adopted, even when selected package guards still pass.

After a Steam update, acquire/verify the new original and replan every selected
package. Passing content/layout guards allows reuse without editing build numbers.
A missing target, changed body/table/native signature or ambiguous match blocks
that package; no guessed retargeting or automatic AI rewrite is part of v1.

Applying requires the game to be closed and Steam to be stable. Stage all output
first, verify originals have not changed, save preimages, and use a recovery
journal while committing root files. Files cannot be replaced atomically as a
group: on failure, roll back only files that still match this transaction's output
and build. A crash or concurrent Steam change leaves the journal/preimages for
recovery instead of overwriting newly changed files. Never patch saves or launch
options as an installation side effect.

## Archive validation

Before planning, the reader must enforce:

- A single root manifest, UTF-8 JSON, no duplicate JSON keys or unknown fields.
- Safe relative paths, no traversal, roots, backslashes, reserved device names,
  links, case aliases, duplicate entries or file/directory collisions.
- Unique operation IDs, payload paths, dependency IDs and check targets; valid
  dependency bounds and no self-dependencies/conflicts.
- All non-manifest files listed in `files`, exact byte counts and SHA-256 hashes,
  and all referenced payloads present. Directory ZIP entries carry no payload.
- At most 4 MiB for the manifest, 100,000 ZIP entries, 256 MiB per payload,
  1 GiB total expanded payload data and eight nested container steps. Enforce
  expanded limits while reading, not only from untrusted ZIP size headers.
- A maximum 2 GiB ZIP file, 64 JSON nesting levels, 1,024 characters and 64
  components per filesystem path, and 200,000 total explicit/implicit ZIP paths.
- Supported container and bytecode adapters for every operation. Unknown features
  fail instead of being ignored or falling back to copying entire archives.

Payload hashes check content integrity, not publisher identity. Mod IDs are stable
package names, not authentication. Compatibility checks are read-only; Apply
uses backups, staged files and a recovery journal.

## Reader API

`SnowPatcher.Core.Packages.ModPackageReader` reads a package without extracting
files or accessing a game installation:

```csharp
var package = ModPackageReader.Read(zipPath, cancellationToken);
var name = package.Manifest.Name;
var bytes = package.ReadPayload("payloads/example.txt", cancellationToken);
```

The returned snapshot has immutable metadata and the ZIP's SHA-256. Reading a
payload later rechecks the archive identity and the payload before returning a
byte copy. Re-add a ZIP if its contents change. Invalid input reports an
`InvalidDataException`; source changes or filesystem errors report an `IOException`.
Cancellation is supported while reading and hashing.

The schema is embedded in the app and evaluated locally with JsonSchema.Net;
the package's `$schema` hint is not fetched. Additional checks cover references,
dependency bounds, native-guard declarations, overlapping SSO edits and instruction
labels. The reader itself does not inspect game files; the compatibility checker
handles targets, dependency resolution and conflicts. A reader-valid package
does not imply executable support or compatibility with an installed game.

See [application behavior](APPLICATION.md) for imports, updates, backups and
recovery, and [mod.io configuration](MODIO.md) for downloads.
