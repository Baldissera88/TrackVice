namespace TrackVice
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    // A customers file locked with a password. Layout: "TVCF", version, iterations (4 bytes), salt (16),
    // IV (16), AES-256-CBC text, then an HMAC-SHA256 over everything before it.
    public static class CustomerFile
    {
        public const int MinPassword = 8;
        public const int Iterations = 200000;
        public const string Extension = ".tvcustomers";
        public const string WrongMessage = "The password is wrong or the file has been changed.";
        const byte Version = 1;
        const int SaltLen = 16, IvLen = 16, MacLen = 32, HeaderLen = 9;
        const int MaxIterations = 5000000;

        public static string PasswordProblem(string password)
        {
            if (password == null || password.Length < MinPassword) return "The password needs at least " + MinPassword + " characters.";
            return null;
        }

        public static bool IsSealed(byte[] data)
        {
            return data != null && data.Length >= 4 && data[0] == (byte)'T' && data[1] == (byte)'V' && data[2] == (byte)'C' && data[3] == (byte)'F';
        }

        public static byte[] Seal(string json, string password)
        {
            string problem = PasswordProblem(password);
            if (problem != null) throw new ArgumentException(problem);
            byte[] salt = Random(SaltLen), iv = Random(IvLen);
            byte[] encKey, macKey;
            Keys(password, salt, Iterations, out encKey, out macKey);
            byte[] cipher;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                byte[] plain = new UTF8Encoding(false).GetBytes(json ?? "");
                using (ICryptoTransform t = aes.CreateEncryptor()) cipher = t.TransformFinalBlock(plain, 0, plain.Length);
                Array.Clear(plain, 0, plain.Length);
            }
            byte[] body = new byte[HeaderLen + SaltLen + IvLen + cipher.Length];
            body[0] = (byte)'T'; body[1] = (byte)'V'; body[2] = (byte)'C'; body[3] = (byte)'F';
            body[4] = Version;
            body[5] = (byte)(Iterations >> 24); body[6] = (byte)((Iterations >> 16) & 255); body[7] = (byte)((Iterations >> 8) & 255); body[8] = (byte)(Iterations & 255);
            Buffer.BlockCopy(salt, 0, body, HeaderLen, SaltLen);
            Buffer.BlockCopy(iv, 0, body, HeaderLen + SaltLen, IvLen);
            Buffer.BlockCopy(cipher, 0, body, HeaderLen + SaltLen + IvLen, cipher.Length);
            byte[] mac = Mac(macKey, body, body.Length);
            byte[] all = new byte[body.Length + MacLen];
            Buffer.BlockCopy(body, 0, all, 0, body.Length);
            Buffer.BlockCopy(mac, 0, all, body.Length, MacLen);
            Array.Clear(encKey, 0, encKey.Length);
            Array.Clear(macKey, 0, macKey.Length);
            return all;
        }

        public static string Open(byte[] data, string password)
        {
            if (!IsSealed(data)) throw new InvalidDataException("This is not a protected customers file.");
            if (data.Length < HeaderLen + SaltLen + IvLen + 16 + MacLen || (data.Length - HeaderLen - SaltLen - IvLen - MacLen) % 16 != 0)
                throw new InvalidDataException(WrongMessage);
            if (data[4] != Version) throw new InvalidDataException("This file was made by a newer version of TrackVice.");
            if (password == null || password.Length == 0) throw new InvalidDataException(WrongMessage);
            int iterations = (data[5] << 24) | (data[6] << 16) | (data[7] << 8) | data[8];
            if (iterations < 1000 || iterations > MaxIterations) throw new InvalidDataException(WrongMessage);
            byte[] salt = new byte[SaltLen], iv = new byte[IvLen];
            Buffer.BlockCopy(data, HeaderLen, salt, 0, SaltLen);
            Buffer.BlockCopy(data, HeaderLen + SaltLen, iv, 0, IvLen);
            byte[] encKey, macKey;
            Keys(password, salt, iterations, out encKey, out macKey);
            int bodyLen = data.Length - MacLen;
            byte[] expect = Mac(macKey, data, bodyLen);
            int diff = 0;
            for (int i = 0; i < MacLen; i++) diff |= expect[i] ^ data[bodyLen + i];
            Array.Clear(macKey, 0, macKey.Length);
            if (diff != 0) { Array.Clear(encKey, 0, encKey.Length); throw new InvalidDataException(WrongMessage); }
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = encKey;
                    aes.IV = iv;
                    int start = HeaderLen + SaltLen + IvLen;
                    using (ICryptoTransform t = aes.CreateDecryptor())
                    {
                        byte[] plain = t.TransformFinalBlock(data, start, bodyLen - start);
                        string text = new UTF8Encoding(false).GetString(plain);
                        Array.Clear(plain, 0, plain.Length);
                        return text;
                    }
                }
            }
            catch (CryptographicException) { throw new InvalidDataException(WrongMessage); }
            finally { Array.Clear(encKey, 0, encKey.Length); }
        }

        // One slow derivation, then two keys for two jobs, so the MAC key is never the cipher key.
        static void Keys(string password, byte[] salt, int iterations, out byte[] encKey, out byte[] macKey)
        {
            byte[] master;
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(password, salt, iterations)) master = kdf.GetBytes(32);
            using (HMACSHA256 h = new HMACSHA256(master))
            {
                encKey = h.ComputeHash(Encoding.ASCII.GetBytes("TVCF encryption"));
                macKey = h.ComputeHash(Encoding.ASCII.GetBytes("TVCF authentication"));
            }
            Array.Clear(master, 0, master.Length);
        }

        static byte[] Mac(byte[] key, byte[] data, int count)
        {
            using (HMACSHA256 h = new HMACSHA256(key)) return h.ComputeHash(data, 0, count);
        }

        static byte[] Random(int n)
        {
            byte[] b = new byte[n];
            using (RandomNumberGenerator r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return b;
        }
    }
}
