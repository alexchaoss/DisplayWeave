using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DisplayWeave
{
    // Finds and downloads a newer DisplayWeave setup from the public GitHub releases over anonymous
    // HTTPS. No GitHub account, token or GitHub CLI is used: trust comes from the release-key signature.
    internal static class Updater
    {
        private const string Repository = "alexchaoss/DisplayWeave";
        private static readonly Regex ReleaseVersion = new Regex(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(-[0-9A-Za-z.-]+)?$");
        // Public halves (CNG ECCPUBLICBLOB, base64) of the ECDSA P-256 keys that sign each release's setup
        // checksum; scripts\sign-release.ps1 -NewKey creates one and pins it here. The
        // private halves never reach GitHub, so a compromised repository or release cannot ship an update.
        internal static readonly string[] ReleaseKeys = { "RUNTMSAAAAATu9pkeZZN3cVNNw4GOUxueHEixTzm9R7e7MN0k3itVRAOfmWB0wJJnw1R+TlDITlNIiV/J1hxl/u9rDcSzX0m", "RUNTMSAAAABogT4jkDtu/qbfUCsR8ytE7STQ0FLahj7oxAj94s5KxTvK1V281m1RpTA09IOntRGyROecHzkhWVGtl2w/UZf0" };

        // Installed copies run from <root>\versions\<version>\; development builds never update.
        internal static string InstallRoot(string executablePath)
        {
            string versions = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(executablePath)));
            if (versions == null || !String.Equals(Path.GetFileName(versions), "versions", StringComparison.OrdinalIgnoreCase)) return null;
            string root = Path.GetDirectoryName(versions);
            return root != null && File.Exists(Path.Combine(root, "current.json")) ? root : null;
        }

        // Numeric order; a final release sorts after its own previews (0.6.0 > 0.6.0-preview).
        internal static int Compare(string left, string right)
        {
            Match a = ReleaseVersion.Match(left ?? ""), b = ReleaseVersion.Match(right ?? "");
            if (!a.Success || !b.Success) return 0;
            for (int i = 1; i <= 3; i++)
            {
                int difference = Int32.Parse(a.Groups[i].Value).CompareTo(Int32.Parse(b.Groups[i].Value));
                if (difference != 0) return difference;
            }
            return b.Groups[4].Success.CompareTo(a.Groups[4].Success);
        }

        // Returns the newest release version from the tag list when it is newer than current; skipped versions failed here before.
        internal static string Newest(string current, IEnumerable<string> tags, ICollection<string> skipped = null)
        {
            string best = null;
            foreach (string tag in tags)
            {
                string version = tag.Trim();
                if (!version.StartsWith("v", StringComparison.Ordinal)) continue; // prototype-v* tags are another app
                version = version.Substring(1);
                if (ReleaseVersion.IsMatch(version) && Compare(version, current) > 0 && (best == null || Compare(version, best) > 0) &&
                    (skipped == null || !skipped.Contains(version))) best = version;
            }
            return best;
        }

        // Tag names of the published releases in a GitHub "list releases" response; drafts are skipped.
        internal static List<string> ReleaseTags(string json)
        {
            object[] releases = new JavaScriptSerializer().DeserializeObject(json) as object[];
            if (releases == null) throw new InvalidDataException("GitHub returned an unexpected release list.");
            List<string> tags = new List<string>();
            foreach (object item in releases)
            {
                Dictionary<string, object> release = item as Dictionary<string, object>;
                object tag, draft;
                if (release != null && release.TryGetValue("tag_name", out tag) && tag is string &&
                    release.TryGetValue("draft", out draft) && false.Equals(draft)) tags.Add((string)tag);
            }
            return tags;
        }

        // Returns the signed and checksum-verified setup of a newer release, or null when this copy is current.
        internal static string DownloadNewer(string current, ICollection<string> skipped, out string version)
        {
            // Without a target-framework attribute, .NET Framework offers only TLS 1.0, which GitHub refuses.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            version = Newest(current, ReleaseTags(Encoding.UTF8.GetString(Download("https://api.github.com/repos/" + Repository + "/releases?per_page=50", 4 << 20))), skipped);
            if (version == null) return null;
            string directory = Path.Combine(Path.GetTempPath(), "DisplayWeaveUpdate");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.CreateDirectory(directory);
            string setup = Path.Combine(directory, "DisplayWeave-Setup-" + version + ".exe");
            // Newest only returns versions matching ReleaseVersion, so the version is safe inside the URL.
            string url = "https://github.com/" + Repository + "/releases/download/v" + version + "/" + Path.GetFileName(setup);
            File.WriteAllBytes(setup + ".sha256", Download(url + ".sha256", 4096));
            File.WriteAllBytes(setup + ".sha256.sig", Download(url + ".sha256.sig", 4096));
            File.WriteAllBytes(setup, Download(url, 64 << 20));
            if (!IsSignedRelease(setup, version, ReleaseKeys))
                throw new InvalidDataException("the " + version + " setup is not signed with a DisplayWeave release key, so it was not installed.");
            return setup;
        }

        // The signature covers the exact .sha256 bytes. That line names the file, and so its version,
        // which stops a validly signed older setup from being republished under a newer tag.
        internal static bool IsSignedRelease(string setup, string version, IEnumerable<string> keys)
        {
            string name = "DisplayWeave-Setup-" + version + ".exe";
            if (!String.Equals(Path.GetFileName(setup), name, StringComparison.Ordinal)) return false;
            FileInfo checksumFile = new FileInfo(setup + ".sha256");
            if (!checksumFile.Exists || checksumFile.Length > 4096) return false;
            byte[] checksum = File.ReadAllBytes(checksumFile.FullName), signature;
            try { signature = Convert.FromBase64String(File.ReadAllText(setup + ".sha256.sig").Trim()); }
            catch (FormatException) { return false; }
            catch (IOException) { return false; }
            bool signed = false;
            foreach (string key in keys)
            {
                try
                {
                    using (CngKey publicKey = CngKey.Import(Convert.FromBase64String(key), CngKeyBlobFormat.EccPublicBlob))
                    using (ECDsaCng ecdsa = new ECDsaCng(publicKey))
                    {
                        ecdsa.HashAlgorithm = CngAlgorithm.Sha256;
                        if (ecdsa.VerifyData(checksum, signature)) { signed = true; break; }
                    }
                }
                catch (CryptographicException) { }
                catch (FormatException) { }
            }
            if (!signed) return false;
            Match line = Regex.Match(Encoding.ASCII.GetString(checksum), "^([0-9a-f]{64})  " + Regex.Escape(name) + "\r?\n?$");
            if (!line.Success) return false;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(setup))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == line.Groups[1].Value;
        }

        // Versions given up on this computer: the launcher went back from them after two failed starts, or the user did.
        internal static HashSet<string> FailedVersions(string root)
        {
            HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
            string health = Path.Combine(root, "health");
            if (Directory.Exists(health))
                foreach (string file in Directory.GetFiles(health, "*.failed")) failed.Add(Path.GetFileNameWithoutExtension(file));
            return failed;
        }

        // The version installed before this one, which the installer records in current.json, while its files are still there.
        internal static string PreviousVersion(string root, string current)
        {
            try
            {
                Dictionary<string, object> pointer = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root, "current.json"))) as Dictionary<string, object>;
                object value;
                string previous = pointer != null && pointer.TryGetValue("previousVersion", out value) ? value as string : null;
                return previous != null && ReleaseVersion.IsMatch(previous) && previous != current &&
                    File.Exists(Path.Combine(root, "versions", previous, "DisplayWeave.exe")) ? previous : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; } // not JSON
            catch (InvalidOperationException) { return null; }
        }

        // Points the launcher back at the previous version. The current one is marked failed, so updates skip it.
        internal static void GoBack(string root, string current, string previous)
        {
            if (!ReleaseVersion.IsMatch(current) || !ReleaseVersion.IsMatch(previous)) throw new ArgumentException("Not a DisplayWeave version.");
            string health = Directory.CreateDirectory(Path.Combine(root, "health")).FullName;
            File.WriteAllText(Path.Combine(health, current + ".failed"), "Gone back to " + previous + " from the tray menu.\r\n");
            string pointer = Path.Combine(root, "current.json"), temporary = pointer + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, "{\r\n    \"version\":  \"" + previous + "\",\r\n    \"executable\":  \"versions/" + previous +
                "/DisplayWeave.exe\",\r\n    \"dataDirectory\":  \"data\"\r\n}\r\n", new UTF8Encoding(false));
            File.Replace(temporary, pointer, null);
        }

        // GitHub's API refuses requests without a User-Agent. Release files arrive through a redirect
        // to GitHub's download host, which HttpWebRequest follows.
        private static byte[] Download(string url, int limit)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "DisplayWeave-updater";
            request.Timeout = request.ReadWriteTimeout = 120000;
            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > limit) throw new InvalidDataException(url + " is larger than expected.");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }
    }
}
