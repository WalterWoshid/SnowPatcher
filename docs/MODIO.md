# mod.io connection

`Add mods...` accepts a SnowRunner mod.io listing link or local ZIPs. A linked
mod is downloaded from its live, approved Windows file. The library saves the
source mod/file IDs, verified ZIP hash, version, enabled state and check time in
`SnowPatcher/Library` under the user's local application data folder.

Checks run at launch and every 30 minutes while the app is open. Successful
results are cached for six hours. A newer live file shows `Update`; the button
fetches fresh metadata and downloads the file. File IDs and release times detect
changes even when the author reuses a version label. Switching the live listing
to an older file shows `Older release` and does not offer an automatic downgrade.
Failed checks show `Retry`, not `Up to date`; HTTP 429 backoff is respected.

Downloads are staged under the library, checked against mod.io's size and MD5,
and against SHA-256 metadata when supplied. ZIP/package validation runs before
the library entry changes. Existing package IDs must match. Previous downloads
are retained, cancellation removes staging files, and invalid downloads keep the
current entry. A missing or changed cached download can be fetched again; a
changed cached file is retained separately. Local source ZIPs are never removed.

Adding a link reuses a row with the same package ID, or identical ZIP bytes.
Source metadata describes the downloaded bytes; it is never attached to a
different local archive. Legacy ZIPs have no package ID, and versions are not
guessed from filenames.
The library lock prevents two app windows from overwriting the same list.
Account subscriptions are not implemented. The read-only
compatibility checks above operate separately from download updates. A downloaded legacy ZIP remains a manual mod.

## Build configuration

The [mod.io API](https://docs.mod.io/restapi/introduction) needs a read-only key
for public browsing/downloads. Players do not enter API keys. Configure the key
when building with `SnowPatcherModioApiKey` or the `SNOWPATCHER_MODIO_API_KEY`
environment variable. On Windows, the app project also imports the optional
`%LOCALAPPDATA%/SnowPatcher/BuildSettings.props`:

```xml
<Project>
  <PropertyGroup>
    <SnowPatcherModioApiKey>YOUR_READ_ONLY_API_KEY</SnowPatcherModioApiKey>
  </PropertyGroup>
</Project>
```

The key becomes assembly metadata in the binary and can be recovered from it;
it provides read-only public access. Never supply a publishing access token.
Keep local settings and credentials outside the repository. Builds without a
valid configured key still support local ZIPs and explain the missing connection
when a mod.io operation is attempted.

`ModioClient` uses GET requests only, verifies the SnowRunner game and Windows
approval, rejects harmful-file verdicts, limits metadata/download sizes and
restricts redirects to HTTPS mod.io API/CDN hosts. Fresh
[download URLs](https://docs.mod.io/restapi/docs/get-modfiles) are obtained for each
update rather than persisting expiring URLs in the library. No access token,
API key or download URL is saved in library records. Updating this library does
not write installed game files or saves; game writes happen only through Apply.
