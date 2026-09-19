# Windows account identity

Account ID is a stable local key used by schedules and browser-profile folders. Display name is a label, not a matching key. `ZoomEmail` is the authoritative email used to select a saved Zoom Desktop account; plus tags are preserved.

The Accounts editor shows and validates Zoom Email. Save changes before switching. A credential reference is optional when using an already signed-in Desktop account or persistent Web profile; switching accounts never asks for a password. The editor does take an account's Zoom password, because a Web profile nobody has signed in joins as a guest and a guest cannot admit anybody. It goes to Windows Credential Manager (`ZoomAutoAdmit/ZoomProfile/<account>`), never to the app's JSON or its log, and the metadata holds only the `wincred:` reference to it. From there it is also kept on the central server, AES-GCM encrypted, so another PC running that person's classes can sign their profile in instead of waiting for them.

Legacy JSON without ZoomEmail still loads through the previous credential-reference path. It is not automatically assigned the credential username: that mapping may be wrong. Set the email explicitly in Accounts. Explicit ZoomEmail always takes precedence over a legacy credential reference. This does not authenticate a browser profile or automate login.

Metadata stays at `%LOCALAPPDATA%\ZoomAutoAdmit\Accounts\accounts.json`. Save uses a same-directory temporary file and replaces the prior file with a `.bak` backup. Duplicate IDs/emails are rejected. Writes by account-manager instances in one process are serialized; this is not a cross-process transaction store. Existing credentials, schedule IDs, and browser profiles are not renamed or deleted.

Each account can also store an optional `DefaultMeetingUrl`. Set or change it in Accounts and press Save. Start Meeting loads the selected account's link automatically; a manual override there affects only that launch. Changing the selected group clears/replaces the previous group's URL. Existing schedules keep their own saved meeting URLs; changing an account default does not rewrite schedules. Complete Zoom HTTPS links, including passcode query parameters, are preserved. Treat the metadata file and its backups as private since invitation links may grant meeting access.

Design references:

Desktop launch tries the existing `zoommtg` URL first. If no launch UI appears and Zoom is still idle on Home, it attempts Home > Join using the numeric meeting ID once. Any existing preview/passcode/join dialog suppresses retries. The fallback verifies 'Don't connect to audio' and 'Turn off my video' before submitting Join. Invitation `pwd` values are not treated as plaintext passcodes. This fallback does not automate the subsequent preview Start/passcode prompt; the existing join-verification stage still requires the meeting to be joined. Pre-Start media handling for the primary-link and Web paths is a separate pending integration.

- [Microsoft CREDENTIALW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw): credential target name, username, and secret blob are distinct fields; a generic credential target is not a Zoom account ID.
- [System.Text.Json property customization](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/customize-properties): explicit metadata fields and string enum persistence.
