# HTTPS with the household's own certificate authority

Issue [#35](https://github.com/joeizang/brilliantApp/issues/35). The household server ([server.md](server.md)) speaks **HTTPS only**, so progress, drafts and Content Packs never cross the Wi-Fi in the clear, and neither app needs a cleartext exception any more.

There is no public domain to get a certificate for, so the household makes its own **certificate authority (CA)** once, uses it to sign the server's certificate, and **installs the CA's public certificate on each device** (a Mac, an Android phone, the Ubuntu laptop if it runs the CLI). A device that trusts the CA trusts the server; one that doesn't refuses to talk to it and says why.

## Set up (once)

On the machine that runs the server:

```bash
scripts/local-ca.sh 192.168.1.20 nuc.local     # the names/addresses your devices will type; none = this machine's LAN address and host name
```

It needs only `openssl` (on macOS and Ubuntu already). It creates:

| File | What it is | Keep it |
|---|---|---|
| `~/.brilliant/ca/ca.key` | the CA's private key (mode 600, in a 700 folder) | **private, and backed up with `~/.brilliant/android/`**. It is never copied anywhere. Whoever holds it can make certificates your devices believe. |
| `~/.brilliant/ca/ca.crt` | the CA's certificate | public |
| `tls/server.pem`, `tls/server.key` | the server's certificate and key (git-ignored; the container mounts this folder read-only) | the key is readable by the container's user, so by anyone on this machine; it only secures this one server |
| `tls/brilliant-ca.crt` | a copy of `ca.crt`: **the file you install on devices** | public |

`BRILLIANT_CA_DIR` and `BRILLIANT_TLS_DIR` move the folders; `--help` lists the options.

The certificate lists `localhost`, `127.0.0.1`, `::1` and `10.0.2.2` (the Android emulator's name for the machine it runs on) besides what you pass. **A device only accepts the server at a name that is on the certificate**, so give the script every address you will type. Give the server a fixed address first (a DHCP reservation in the router); if it changes, run the script again with the new one and pair the devices again.

Then start the server:

```bash
cp .env.example .env     # first time only; set POSTGRES_PASSWORD
docker compose up -d --build
```

The server now listens on **8443** (`SERVER_PORT` in `.env` changes the port on the host) and **refuses to start** without the files, naming the missing one. Check it, trusting only the CA:

```bash
curl --cacert tls/brilliant-ca.crt https://localhost:8443/health
# {"status":"ok"}
```

## Trust the CA on each device (your part)

### Mac

Double-click `tls/brilliant-ca.crt` (Keychain Access opens and adds it), or from a terminal:

```bash
security add-trusted-cert -r trustRoot -p ssl -k ~/Library/Keychains/login.keychain-db tls/brilliant-ca.crt
```

macOS asks for your password. Check it with Apple's own curl, which uses the keychain:

```bash
/usr/bin/curl https://localhost:8443/health
```

The Mac app and the CLI then trust the server too. To undo it, delete the **Brilliant household CA** certificate in Keychain Access.

### Android

1. Set a screen lock if there isn't one (Android won't install a CA without it).
2. Get the file onto the phone: `adb push tls/brilliant-ca.crt /sdcard/Download/`, or a USB transfer or cloud drive.
3. **Settings → Security and privacy → More security settings → Install from device storage → CA certificate** (on other makers' phones: Encryption & credentials → Install a certificate → CA certificate). Accept the warning, pick `brilliant-ca.crt`. It appears under *Trusted credentials → User*.
4. **Install the new APK** (`scripts/build-apk.sh --install`, [android.md](android.md)): older builds allowed cleartext and don't trust user-installed CAs, so they can't talk to this server.

Android keeps a "network may be monitored" notice while a user CA is installed; that is expected. The same steps work on an emulator.

### The Ubuntu laptop, or any other computer running the CLI

```bash
sudo cp tls/brilliant-ca.crt /usr/local/share/ca-certificates/brilliant-ca.crt && sudo update-ca-certificates
```

## Pair over HTTPS

The address is now `192.168.1.20:8443`; typing a bare host means `https://` and port 8443, and an `http://` address is refused. Pairing and everything after it work as before ([server.md](server.md#pair-a-device)).

**Devices paired before this change** remember `http://…:8080` and can't reach the server. On each: **Tracks → Sync → Forget this server**, then pair again with the new address. Nothing is lost: progress lives in each device's own log and on the server, and syncs again after pairing ([sync.md](sync.md)). The old device entries stay on the server until you revoke them ([server.md](server.md#manage-devices)).

## What the apps say

| Message | Meaning |
|---|---|
| "This device doesn't trust the server's certificate" | The CA isn't installed on this device (or, on Android, this is an older APK). It is also what you get when the address typed isn't on the certificate: check `scripts/local-ca.sh --check`, which lists the names. |
| "Couldn't reach the server" | The network, not the certificate: the server is off, the device is on another network, or the port is wrong. |

## Renewing

The server certificate lasts 825 days (Apple refuses longer), the CA ten years. Run `scripts/local-ca.sh` again with the same names to renew: **the CA is kept and devices need nothing**. Then `docker compose up -d --force-recreate server`.

`scripts/local-ca.sh --check` reports the expiry and exits non-zero if it is within 30 days. The server also prints a warning at start when the certificate is within 30 days of expiring, and **refuses to start with an expired one** (every device would refuse it anyway). When the CA itself expires, or if `ca.key` is ever exposed, delete `~/.brilliant/ca`, run the script again and install the new CA on every device.

## What this does and doesn't protect

It encrypts traffic between devices and the server and lets a device be sure it's talking to your server rather than another machine on the Wi-Fi. It doesn't make the server safe to put on the internet: don't forward port 8443 from the router. A user-installed CA is trusted for **every** site on that device, which is why `ca.key` must stay private and off the server.

## Tests and what is yours to verify

Automated: `tests/Brilliant.Server.Tests/TlsTests.cs` runs the real script, starts Kestrel with what it made, and connects as a device that trusts only the CA (by IP and by name) and as one that doesn't; checks the certificate's names, usage and lifetime, that re-issuing keeps the CA, and that the compose file, the Android config and the Mac plist have no cleartext allowance. The Core tests cover the address rules and the "certificate not trusted" classification, and the Docker image was run with certificates from the script (HTTPS answers with the CA, a plain request gets nothing, a missing certificate stops the server with a clear message).

**Joseph's part, the acceptance check for #35:** install the CA on the Mac and on the Samsung phone as above, then pair each over the home network and sync. Not exercised here: a real Android device's user-CA store (the `network_security_config` is built into the APK and covered by a test, but only a phone proves Android honours it for this app's HTTP stack), and `.local` names on Android (Android may not resolve them; use the IP).

## Where the code is

| Path | Purpose |
|---|---|
| `scripts/local-ca.sh` | creates the CA and issues/renews/checks the server certificate |
| `src/Brilliant.Server/ServerTls.cs` | loads the certificate, refuses an expired one, listens on HTTPS only |
| `docker-compose.yml`, `src/Brilliant.Server/Dockerfile` | mount `tls/` at `/tls`, publish 8443 |
| `src/Brilliant.Core/Sync/ServerAddress.cs`, `UntrustedCertificate.cs` | https-only addresses; tells an untrusted certificate from an absent server |
| `src/Brilliant.App/Platforms/Android/Resources/xml/network_security_config.xml` | no cleartext; trusts system and user CAs |
| `src/Brilliant.App/Platforms/MacCatalyst/Info.plist` | no App Transport Security exceptions |
