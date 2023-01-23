using System;
using System.Runtime.InteropServices;
using System.IO;
using CipherMan;
using Chilkat;

namespace SADBSConveyorLib
{
    [GuidAttribute("95193FDE-87D8-4AE8-B4DF-142DCB989875")]
    public class SADEncryption
    {
        //  16 bytes of key for 128-bit encryption.
        private const string key = "51AAFBFE86635521171EA2F930788108A7F784C762FB93719355CEC304FE1CF3";

        //  The IV is equal to the block size of the encryption algorithm.
        private const string iv = "12B12D6C644C573CD790B90865A3A754";
        /// <summary>
        /// Encrypts a file using AES algorithm and saves to another file
        /// </summary>
        /// <param name="FileFrom">Path of file to encrypt.</param>
        /// <param name="FileTo">Encrypted output file save path.</param>
        /// <returns>true for success, false for failure.</returns>
        /// 

        protected Crypt2 Crypt;
        protected crc32 CRC;

        public SADEncryption()
        {
            Crypt = new Crypt2();
            CRC = new crc32();

            try
            {
                if (!Crypt.UnlockComponent("MOODME.CB4022020_5zhmnCSp667g"))
                {
                    if (Crypt.LastErrorXml != String.Empty)
                    {
                        throw new Exception("SADEncryption: Crypt2 Unlock Error: " + (Crypt.LastErrorText));
                    }

                    throw new Exception("SADEncryption: Unexpected Crypt2 Unlock Error (Unknown): ");
                }
            }
            catch
            {
                throw new Exception("SADEncryption: Unexpected Crypt2 Unlock Error");
            }
        }


        public bool Encrypt(string FileFrom, string FileTo)
        {
            Crypt.CryptAlgorithm = "aes";
            Crypt.CipherMode = "ecb";
            Crypt.KeyLength = 256;

            //  Set the key.
            Crypt.SetEncodedKey(key, "hex");

            //  Set the IV
            Crypt.SetEncodedIV(iv, "hex");

            // Get file size of un-encrypted file
            int fileSize = 0;
            try
            {
                FileInfo fi = new FileInfo(FileFrom);
                fileSize = (int)fi.Length;
            }
            catch (Exception ex)
            {
                throw new Exception("SADBSConveyor: Unexpected Encryption(FileSize) Error: " + ex.Message);
            }

            if (fileSize == 0)
                throw new Exception("SADBSConveyor: File size of source file is Zero");

            // Get CRC of un-encrypted file
            int crc = 0;
            try
            {
                crc = CRC.crcFile(FileFrom);
            }
            catch (Exception ex)
            {
                throw new Exception("SADBSConveyor: Unexpected Encryption(CRC) Error: " + ex.Message);
            }

            if (crc == 0)
                throw new Exception("SADBSConveyor: CRC of source file is Zero");

            try
            {
                // NOTE: there is no header for this encryption
                using (FileStream outStream = new FileStream(FileTo, FileMode.Create))
                {
                    using (BinaryWriter writer = new BinaryWriter(outStream))
                    {
                        // AES Encrypt the file
                        using (FileStream inStream = new FileStream(FileFrom, FileMode.Open))
                        {
                            using (BinaryReader reader = new BinaryReader(inStream))
                            {
                                // How many blocks of data are we reading in
                                const int bufsz = 10240;
                                int blocks = fileSize / bufsz;
                                int remainder_bytes = fileSize % bufsz;

                                Crypt.FirstChunk = false;
                                Crypt.LastChunk = false;
                                for (int i = 0; i < blocks; i++)
                                {
                                    // Are we encrypting 1st Chunk
                                    if (i == 0)
                                        Crypt.FirstChunk = true;
                                    else
                                        Crypt.FirstChunk = false;

                                    // Are we encrypting last Chunk? this is in case the filesize is exactly divisible by the buffer size
                                    if (remainder_bytes == 0 && i == (blocks - 1))
                                        Crypt.LastChunk = true;

                                    byte[] buf = new byte[bufsz];
                                    int sz = reader.Read(buf, 0, bufsz);

                                    byte[] enbuf = Crypt.EncryptBytes(buf);
                                    writer.Write(enbuf, 0, enbuf.Length);
                                }

                                // If file was smaller bufsz then we are encrypting the first chuck
                                if (blocks == 0)
                                    Crypt.FirstChunk = true;

                                // If remainder_bytes is greated than zero then we're encrypting the last chuck
                                if (remainder_bytes > 0)
                                {
                                    Crypt.LastChunk = true;
                                    byte[] buf = new byte[remainder_bytes];
                                    int sz = reader.Read(buf, 0, remainder_bytes);

                                    byte[] enbuf = Crypt.EncryptBytes(buf);
                                    writer.Write(enbuf, 0, enbuf.Length);
                                }

                                reader.Close();
                            }
                        }
                        writer.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("SADBSConveyor: Error Encrypting File: " + ex.Message);
            }

            return true;

        }

        /// <summary>
        /// DMX Configurator Encryption. 
        /// Encrypts a file using AES algorithm and saves to another file.
        /// </summary>
        /// <param name="FileFrom">Path of file to encrypt.</param>
        /// <param name="FileTo">Encrypted output file save path.</param>
        /// <returns>true for success, false for failure.</returns>
        public bool EncryptConfigurator(string FileFrom, string FileTo)
        {
            return Encrypt(FileFrom, FileTo);
        }

        public bool Decrypt(string FileFrom, string FileTo)
        {
            Crypt.CryptAlgorithm = "aes";
            Crypt.CipherMode = "ecb";
            Crypt.KeyLength = 128;

            uint magicNumber = 0;
            uint decryptFileSize = 0;
            int crc = 0;
            int fileSize = 0;

            //  Set the key.
            Crypt.SetEncodedKey(key, "hex");

            //  Set the IV
            Crypt.SetEncodedIV(iv, "hex");

            try
            {
                FileInfo fi = new FileInfo(FileFrom);
                fileSize = (int)fi.Length - 12;
            }
            catch (Exception ex)
            {
                throw new Exception("SADBSConveyor: Unexpected decryption(FileSize) Error: " + ex.Message);
            }

            if (fileSize == 0)
                throw new Exception("SADBSConveyor: File size of source file is Zero");

            try
            {

                using (FileStream outStream = new FileStream(FileTo, FileMode.Create))
                {
                    using (BinaryWriter writer = new BinaryWriter(outStream))
                    {

                        // AES decrypt the file
                        using (FileStream inStream = new FileStream(FileFrom, FileMode.Open))
                        {
                            using (BinaryReader reader = new BinaryReader(inStream))
                            {
                                byte[] buft = new byte[4];
                                int szt = reader.Read(buft, 0, 4);
                                magicNumber = BitConverter.ToUInt32(buft, 0);
                                szt = reader.Read(buft, 0, 4);
                                decryptFileSize = BitConverter.ToUInt32(buft, 0);
                                szt = reader.Read(buft, 0, 4);
                                crc = BitConverter.ToInt32(buft, 0);

                                // How many blocks of data are we reading in
                                const int bufsz = 10240;
                                int blocks = fileSize / bufsz;
                                int remainder_bytes = fileSize % bufsz;

                                Crypt.FirstChunk = false;
                                Crypt.LastChunk = false;
                                for (int i = 0; i < blocks; i++)
                                {
                                    // Are we decrypting 1st Chunk
                                    if (i == 0)
                                        Crypt.FirstChunk = true;
                                    else
                                        Crypt.FirstChunk = false;

                                    // Are we decrypting last Chunk? this is in case the filesize is exactly divisible by the buffer size
                                    if (remainder_bytes == 0 && i == (blocks - 1))
                                        Crypt.LastChunk = true;

                                    byte[] buf = new byte[bufsz];
                                    int sz = reader.Read(buf, 0, bufsz);

                                    byte[] enbuf = Crypt.DecryptBytes(buf);
                                    writer.Write(enbuf, 0, enbuf.Length);
                                }

                                // If file was smaller bufsz then we are encrypting the first chuck
                                if (blocks == 0)
                                    Crypt.FirstChunk = true;

                                // If remainder_bytes is greated than zero then we're encrypting the last chuck
                                if (remainder_bytes > 0)
                                {
                                    Crypt.LastChunk = true;
                                    byte[] buf = new byte[remainder_bytes];
                                    int sz = reader.Read(buf, 0, remainder_bytes);

                                    byte[] enbuf = Crypt.DecryptBytes(buf);
                                    writer.Write(enbuf, 0, enbuf.Length);
                                }

                                reader.Close();
                            }
                        }
                        writer.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("SADBSConveyor: Error Decrypting File: " + ex.Message);
            }

            try
            {
                if (crc != CRC.crcFile(FileTo))
                    throw new Exception("SADBSConveyor: CRC value of decrypted file does not match");
            }
            catch (Exception ex)
            {
                throw new Exception("SADBSConveyor: Unexpected Encryption(CRC) Error: " + ex.Message);
            }

            return true;
        }
    }
}

