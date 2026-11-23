namespace TrackVice.Tests
{
    using System;
    using System.IO;

    public static class CustomerFileTests
    {
        const string Pw = "correct horse 42";

        static string TryOpen(byte[] data, string pw)
        {
            try { CustomerFile.Open(data, pw); return null; }
            catch (InvalidDataException ex) { return ex.Message; }
        }

        public static void Run()
        {
            string json = "{\"customers\":[{\"name\":\"Contoso\"}]}";
            byte[] sealedData = CustomerFile.Seal(json, Pw);
            T.Check(CustomerFile.IsSealed(sealedData), "sealed file has the magic");
            T.Check(!CustomerFile.IsSealed(System.Text.Encoding.UTF8.GetBytes(json)), "plain json is not sealed");
            T.Eq(json, CustomerFile.Open(sealedData, Pw), "round trip");
            T.Eq("Kunde æøå", CustomerFile.Open(CustomerFile.Seal("Kunde æøå", Pw), Pw), "round trip of text with non-ASCII");
            T.Check(System.Text.Encoding.UTF8.GetString(sealedData).IndexOf("Contoso") < 0, "text is not readable in the file");
            T.Eq(CustomerFile.WrongMessage, TryOpen(sealedData, "another password"), "wrong password");
            T.Eq(CustomerFile.WrongMessage, TryOpen(sealedData, ""), "empty password");

            // header 4..8 (version and count), salt 9..24, IV 25..40, cipher, MAC at the end
            int[] spots = { 5, 8, 9, 24, 25, 40, 41, sealedData.Length - 33, sealedData.Length - 32, sealedData.Length - 1 };
            string[] names = { "header", "header", "salt", "salt", "IV", "IV", "ciphertext", "ciphertext", "MAC", "MAC" };
            for (int i = 0; i < spots.Length; i++)
            {
                byte[] c = (byte[])sealedData.Clone();
                c[spots[i]] ^= 1;
                T.Eq(CustomerFile.WrongMessage, TryOpen(c, Pw), "changed " + names[i] + " byte " + spots[i]);
            }
            byte[] cut = new byte[sealedData.Length - 1];
            Array.Copy(sealedData, cut, cut.Length);
            T.Check(TryOpen(cut, Pw) != null, "cut file is refused");
            byte[] magic = (byte[])sealedData.Clone();
            magic[0] ^= 1;
            T.Check(TryOpen(magic, Pw) != null, "changed magic is refused");

            bool refused = false;
            try { CustomerFile.Seal(json, "short"); } catch (ArgumentException) { refused = true; }
            T.Check(refused, "short password refused");
            T.Check(CustomerFile.PasswordProblem("1234567") != null, "password problem for 7 characters");
            T.Check(CustomerFile.PasswordProblem("12345678") == null, "no problem for 8 characters");

            byte[] again = CustomerFile.Seal(json, Pw);
            bool same = again.Length == sealedData.Length;
            if (same) { same = true; for (int i = 0; i < again.Length; i++) if (again[i] != sealedData[i]) { same = false; break; } }
            T.Check(!same, "two seals of the same data differ");
            T.Eq(json, CustomerFile.Open(again, Pw), "both open");

            // The old plain file still goes through the existing import.
            CustomerBook book = CustomerBook.Demo();
            string plain = book.Export(false);
            T.Check(!CustomerFile.IsSealed(System.Text.Encoding.UTF8.GetBytes(plain)), "plain export is not sealed");
            T.Check(book.Import(plain) != null, "plain export imports");
            string linked = book.Export(true);
            T.Eq(linked, CustomerFile.Open(CustomerFile.Seal(linked, Pw), Pw), "sealed export round trip");
        }
    }
}
