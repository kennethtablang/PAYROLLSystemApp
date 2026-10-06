// Signs release checksums so the in-app updater can tell a release you built
// from one uploaded by anyone else who got hold of the GitHub repository.
//
//   dotnet run tools/release-signing.cs -- newkey
//       Creates the private signing key (once, per developer PC) and prints the
//       public key to paste into UpdateService.ReleasePublicKey.
//   dotnet run tools/release-signing.cs -- sign <file>
//       Writes <file>.sig. build-installer.ps1 calls this for the .sha256.
//
// The private key lives outside the repository, in
// %APPDATA%\PayrollMS-Release\signing-key.pem. Back it up somewhere private
// (USB drive, password manager). If it is lost, generate a new one, put the new
// public key in the app, and install that version by hand once.

using System.Security.Cryptography;

var keyFile = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PayrollMS-Release", "signing-key.pem");

switch (args.FirstOrDefault())
{
    case "newkey":
        if (File.Exists(keyFile))
        {
            Console.Error.WriteLine($"A key already exists at {keyFile}. Delete it first if you really mean to replace it.");
            return 1;
        }

        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
            File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());

            Console.WriteLine($"Private key written to {keyFile} - back it up privately, never commit it.");
            Console.WriteLine();
            Console.WriteLine("Public key for UpdateService.ReleasePublicKey:");
            Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
        }
        return 0;

    case "sign" when args.Length == 2:
        if (!File.Exists(keyFile))
        {
            Console.Error.WriteLine($"No signing key at {keyFile}. Restore your backup, or run: dotnet run tools/release-signing.cs -- newkey");
            return 1;
        }

        using (var key = ECDsa.Create())
        {
            key.ImportFromPem(File.ReadAllText(keyFile));
            var signature = key.SignData(File.ReadAllBytes(args[1]), HashAlgorithmName.SHA256);
            File.WriteAllText(args[1] + ".sig", Convert.ToBase64String(signature) + "\n");
            Console.WriteLine($"Signed {Path.GetFileName(args[1])}");
        }
        return 0;

    default:
        Console.Error.WriteLine("Usage: release-signing.cs newkey | sign <file>");
        return 2;
}
