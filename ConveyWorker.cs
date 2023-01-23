using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using SADBSConveyorLib;
using SADBSConveyorCSharpLib;
using DMXConverter;
using System.Diagnostics;
using ProtocolHandler;
using DMXCommon;
using Amazon.S3;
using Amazon.S3.Model;
using CipherMan;

namespace SADBSConveyor
{
    public class ConveyWorker
    {
        // don't ever ever ever change GAIN_SETTING
        private const int GAIN_SETTING = 0;
        // don't ever ever ever change GAIN_SETTING

        private const string FILE_EXTENSION_ENC = ".enc";
        private const string FILE_EXTENSION_MP2 = ".mp2";
        private const int MAX_METADATA_LENGTH = 79;

        private CancellationToken CancelToken;
        private Logging Log;
        private cPlaylistX Playlist;
        private MP2Converter MP2;
        private readonly Data Data = new Data();
        private readonly crc32 CRC = new crc32();

        private int TaskNumber;
        private string PathTempFolder;
        private bool loggedServiceStopping;

        public long PlaylistQueueReferenceID
        {
            get { return this.Playlist.Playlist.QueueReference.Id; }
        }

        private SyncMule ftpClient;
        private cTCPClient tcpClient;

        public void Start(cPlaylistX playlist, Logging log, int taskNumber, CancellationToken cancelToken)
        {
            this.CancelToken = cancelToken;
            this.Log = log;
            this.loggedServiceStopping = false;
            this.Playlist = playlist;
            this.TaskNumber = taskNumber;
            bool bGotChannelInfo = false;

            try
            {
                // note: must be set prior to Init();
                this.PathTempFolder = Path.Combine(Settings.PathTempFolder, taskNumber.ToString());
             
                Init();

                GetDefaultPaths(); // this is so they can update paths in DB without restarting the service.

                LogEntry("Starting Playlist Processing:" + playlist.Playlist.PlaylistInstanceId);
                UpdateServiceStatus("Starting Playlist Processing:" + playlist.Playlist.PlaylistInstanceId);

                var success = false;

                if (ServiceStopping()) return;

                DeleteTempFiles();

                bGotChannelInfo = GetChannelInfo(playlist.Playlist.SourceSystemId);
                success = bGotChannelInfo;

                if (success)
                {
                    foreach (cSongX song in playlist.objDistinctSongs)
                    {
                        string fileName;
                        var foundMatch = false;
                        success = false;

                        if (!FindSongMatch(song, out foundMatch)) break;

                        UpdateServiceStatus(string.Concat("Processing Song: ", song.SongTitle, " - ", song.SongID));

                        if (foundMatch)
                        {
                            LogEntry("Existing song located: skipping transcode - PublishedFilename(" + song.PublishedSongID + ")");
                        }
                        else
                        {
                            if (!GetSourceFile(song, out fileName)) break;

                            if (!MP2Encode(song, GAIN_SETTING, ref fileName)) break;

                            if (!GetNewSongID(song)) break;

                            if (Settings.EnableEncryption)
                            {
                                if (!EncryptAndRenameToFinal(song, ref fileName)) break;
                            }
                            else
                            {
                                if (!RenameToFinal(song, ref fileName)) break;
                            }

                            string tempFilePath = Path.Combine(this.PathTempFolder, fileName);

                            if (File.Exists(tempFilePath))
                            {
                                var info = new FileInfo(tempFilePath);
                                song.PublishedFileSizeInBytes = info.Length;
                            }

                            string publishedFilePath = string.Empty;
                            string crc = string.Empty;

                            if (Settings.UPLOADMODE.ftp == Settings.UploadMode)
                            {
                                    string ftpContentRoot;
                                    string ftpFinalDirPath;

                                    ftpContentRoot = PreFormatFtpPath(Settings.FTPContentRoot);

                                    // rest of path /'s
                                    if (ftpContentRoot.Substring(ftpContentRoot.Length - 1) == "/")
                                    {
                                        ftpFinalDirPath = ftpContentRoot + GetDestFolderName(fileName) + "/";
                                    } else {
                                        ftpFinalDirPath = ftpContentRoot + "/" + GetDestFolderName(fileName) + "/";
                                    }

                                    // FTP Transfer
                                    FTPConnect();
                                    TransferFileToFTP(fileName, this.PathTempFolder, ftpFinalDirPath);

                                    publishedFilePath = ftpFinalDirPath + fileName;
                                    crc = CRC.crcFile(tempFilePath).ToString("X").ToLower();

                            }

                            if(Settings.UPLOADMODE.unc == Settings.UploadMode)
                            {
                                    // UNC Transfer
                                    if (!EnsureDestinationFolder(fileName)) break;

                                    if (!CopyContentToDestination(fileName, ref crc, ref publishedFilePath)) break;

                            }

                            if (Settings.UPLOADMODE.netcred == Settings.UploadMode)
                            {

                                    // upload network access with credentials
                                    var finalNetPath = @"\\" +  Settings.NetCredComputerName + "\\" + Path.Combine( Settings.NetCredContentSavePath, GetDestFolderName(fileName), fileName);
                                    var localFilePath = Path.Combine(this.PathTempFolder, fileName);

                                    if (!UploadWithCreds(localFilePath, finalNetPath, ref crc)) break;
                                    publishedFilePath = finalNetPath;
                            }

                            song.PublishedFilepath = publishedFilePath;
                            song.CRC = crc;

                            if (!SaveSongToDB(song, playlist.Playlist.SourceSystemId)) break;

                            LogEntry("Song Completed (" + fileName + ")");
                        }

                        if (ServiceStopping())
                        {
                            success = false; // this will help to make sure that unfinished playlists are not saved to the DB and final playlistxmls are not created.
                            break;
                        }
                        else
                        {
                            success = true;
                        }

                    } // foreach (cSongX song in playlist.objDistinctSongs)
                } // if (success)

                // the following statements use short circuit &&'s
                if (success && !SavePlaylistToDB()) success = false;

                if (success && !SavePlaylistXMLs(playlist))
                { 
                    success = false;
                    LogEntry("XML output create failed.");
                } 
                
                if (success)
                {
                    LogEntry("Playlist Completed");
                    playlist.PlaylistClient.UpdateStatusCompleted(this.PlaylistQueueReferenceID);
                    UpdateServiceStatus("Playlist Completed: " + playlist.Playlist.PlaylistInstanceId);
                }
                else
                {
                    LogEntry("Playlist Failed");
                    UpdateServiceStatus("Playlist Failed: " + playlist.Playlist.PlaylistInstanceId);  
                }

                if (!cancelToken.IsCancellationRequested)
                {
                    if (bGotChannelInfo)
                    {
                        // delete downloaded playlist xml
                        playlist.PlaylistClient.DeletePlaylistXML(playlist.Playlist, playlist.XmlPath);
                    }
                    else
                    {
                        LogEntry("!!! DID NOT GET channel info !!!");
                        LogEntry("!!! NOT Deleting local playlist XML (IPL - PlaylistID GUID): " + playlist.Playlist.Id + " !!!");
                        LogEntry("!!! Playlist XML file renamed with .fail extension !!!");
                    }
                }
            }
            catch (Exception ex)
            {
                LogEntry(string.Concat("Playlist Convey Error: " + ex.Message + " " + ex.StackTrace));
                // delete downloaded playlist xml
                playlist.PlaylistClient.DeletePlaylistXML(playlist.Playlist, playlist.XmlPath);
            }
            finally
            {
                CleanUp();
            }
        }

        private void DeleteTempFiles()
        {
            try
            {
                Array.ForEach(Directory.GetFiles(this.PathTempFolder), File.Delete);
            }
            catch{ }
        }

        private void Init()
        { 
            this.Data.Server = Settings.DatabaseServer;
            this.Data.Database = Settings.DatabaseName;
            this.Data.UserName = Settings.DatabaseUserName;
            this.Data.Password = Settings.DatabasePassword;

            this.MP2 = new MP2Converter();
            this.MP2.ErrLog = Path.Combine(PathTempFolder, "dbpoweramp.log");

            if (Settings.UPLOADMODE.ftp == Settings.UploadMode)
            {
                tcpClient = new cTCPClient
                {
                    HostName = Settings.FTPIP,
                    Port = Settings.CRCPort
                };
            }
        }

        private string PreFormatFtpPath(string rootPath)
        {
            string rawPath;

            if (rootPath.Length == 0)
            {
                rawPath = "/";
            }
            else
            {
                rawPath = rootPath;
            }

            // beginning of path /
            if (rawPath.Substring(0, 1) != "/")
            {
                rawPath = string.Concat("/", rawPath);
            }

            return rawPath;
        }

        private bool UpdateServiceStatus(string status)
        {
            try
            {
                Data.UpdateServiceStatus(status);
                return true;
            }
            catch (Exception ex)
            {
                LogEntry("Error occured calling Update Service Status database: " + ex.Message);
                return false;
            }
        }

        private bool GetDefaultPaths()
        {
            try
            {
                Data.GetDefaultSettingsPaths(ref Settings.PathDestinationRoot, ref Settings.PathXMLRoot);
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatusFail("GetNextPublishedSongID (ERROR): " + ex.Message);
                return false;
            }
        }

        private bool GetNewSongID(cSongX song)
        {
            try
            {
                song.PublishedSongID = this.Data.GetNextPublishedSongID();
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatusFail("GetNextPublishedSongID (ERROR): " + ex.Message);
                return false;
            }
        }

        private bool SaveSongToDB(cSongX song, int sourceSystemID)
        {
            LogEntry("Saving song to DB: " + song.SongTitle);

            try
            {
                Data.SaveSong(song, sourceSystemID);
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatusFail("SaveSongToDB (ERROR): " + ex.Message);
                return false;
            }
        }

        private bool FindSongMatch(cSongX song, out bool foundMatch)
        {
            try
            {
                foundMatch = this.Data.FindSongMatch(song);
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatusFail("FindSongMatch (ERROR): " + ex.Message);
                foundMatch = false;
                return false;
            }
        }

        private bool GetChannelInfo(int sourceSystemID)
        {
            try
            {
                int idChannel = -1;
                var channelNumber = string.Empty;
                var zoneID = string.Empty;

                this.Data.GetChannel(this.Playlist.Playlist.ProgramId.ToString(), sourceSystemID, ref idChannel, ref channelNumber, ref zoneID);

                this.Playlist.IDChannel = idChannel;
                this.Playlist.ChannelNumber = channelNumber;
                this.Playlist.ZoneID = zoneID;

                return true;
            }
            catch (Exception ex)
            {
                LogEntry("Please check MultiChoiceChannel table in database to ensure channel row exists. \n" + ex.Message);
                this.Playlist.PlaylistClient.RenamePlaylistXMLtoDotFail(this.Playlist.Playlist, this.Playlist.XmlPath);
                LogEntry("skipping endpoint fail status notification");

                return false;
            }
        }

        private bool SavePlaylistToDB()
        {
            LogEntry("Saving Playlist to DB: " + this.Playlist.Name);

            try
            {
                Data.SavePlaylist(this.Playlist);
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatusFail("SavePlaylistToDB (ERROR): " + ex.Message);
                return false;
            }
        }

        private bool SavePlaylistXMLs(cPlaylistX playlist)
        {

            PBXML XMLOut = null;
            var xmlFilePath = string.Empty;
            var xmlFileName = string.Empty;
            var channelPath = string.Empty;
            var tempXMLPath = string.Empty;
            string crcNotUsed = null;

            int curDayOfMonth = -1; // this is all we need, every time it changes, start a new xml document.

            try
            {
                LogEntry("Creating output XML files");
                UpdateServiceStatus("Creating output XML files: " + playlist.Name);

                // sanity check, make sure references are in the correct order.
                Playlist.Playlist.DateTimeSequence.References.Sort((x, y) => x.Begin.CompareTo(y.Begin));

                foreach (var reference in Playlist.Playlist.DateTimeSequence.References)
                {
                    // the dawn of a new day, create new xml document.
                    if (curDayOfMonth != reference.Begin.Day)
                    {
                        // save the previoue day's xml
                        // if previous day is not -1
                        if (-1 != curDayOfMonth)
                        {
                            switch (Settings.UploadMode)
                            {
                                case Settings.UPLOADMODE.ftp:
                                    // FTP destination
                                    // first, save to temp working folder
                                    var tempXMLFilePath = Path.Combine(tempXMLPath, xmlFileName);
                                    XMLOut.Save(tempXMLFilePath);

                                    // second, upload to final FTP folder
                                    FTPConnect();
                                    TransferFileToFTP(xmlFileName, tempXMLPath, channelPath);

                                    break;

                                case Settings.UPLOADMODE.unc:
                                    // save to final UNC path
                                    XMLOut.Save(xmlFilePath);

                                    break;

                                case Settings.UPLOADMODE.netcred:
                                    // NET with credentials destination
                                    // first, save to temp working folder
                                    var tempCredXMLFilePath = Path.Combine(tempXMLPath, xmlFileName);
                                    XMLOut.Save(tempCredXMLFilePath);

                                    // upload network access with credentials
                                    var finalNetPath = @"\\" + Settings.NetCredComputerName + "\\" + Path.Combine(Settings.NetCredXMLSavePath, Playlist.ChannelNumber, Path.GetFileName(xmlFileName));
                                    if (!UploadWithCreds(tempCredXMLFilePath, finalNetPath, ref crcNotUsed)) return false;

                                    break;
                            }

                        }

                        curDayOfMonth = reference.Begin.Day;

                        if (reference.Begin.Day == 20)
                        {
                            UpdateServiceStatus("Creating output XML files: " + playlist.Name);
                        }

                        // start fresh
                        XMLOut = new PBXML(playlist.ProgramID.ToString(), "true");

                        // yyyy_MM_dd_HH_mm_ss
                        // hour, minutes, seconds hardcoded to midnight(00_00_00)
                        xmlFileName = reference.Begin.ToString("yyyy_MM_dd_00_00_00") + ".xml";

                        switch (Settings.UploadMode)
                        {
                            case Settings.UPLOADMODE.ftp:
                                // FTP destination channel path
                                channelPath = Path.Combine(Settings.FTPXMLRoot.Replace("/","\\"), playlist.ChannelNumber).Replace('\\', '/') + "/";

                                // working directory path
                                tempXMLPath = Path.Combine(Settings.PathTempFolder, this.TaskNumber.ToString());
                                if (!Directory.Exists(tempXMLPath))
                                {
                                    Directory.CreateDirectory(tempXMLPath);
                                }
                                break;

                            case Settings.UPLOADMODE.unc:
                                // UNC destination
                                channelPath = Path.Combine(Settings.PathXMLRoot, playlist.ChannelNumber);

                                if (!Directory.Exists(channelPath))
                                {
                                    Directory.CreateDirectory(channelPath);
                                }

                                xmlFilePath = Path.Combine(channelPath, xmlFileName);
                                break;

                            case Settings.UPLOADMODE.netcred:


                                tempXMLPath = Path.Combine(Settings.PathTempFolder, this.TaskNumber.ToString());
                                break;

                        }

                    }

                    // note: these have been pre-sorted in cSongsX.populate()
                    var song = Playlist.objDistinctSongs.Item(reference.EntryId - 1);
                    AddSongToXML(song, XMLOut);
                }

                // save the very last days XML
                if (null != XMLOut)
                {

                    switch (Settings.UploadMode)
                    {
                        case Settings.UPLOADMODE.ftp:
                            // FTP destination
                            // first, save to temp working folder
                            var tempXMLFilePath = Path.Combine(tempXMLPath, xmlFileName);
                            XMLOut.Save(tempXMLFilePath);

                            // second, upload to final FTP folder
                            FTPConnect();
                            TransferFileToFTP(xmlFileName, tempXMLPath, channelPath);

                            break;

                        case Settings.UPLOADMODE.unc:
                                // save to final UNC path
                            XMLOut.Save(xmlFilePath);

							break;
 
                        case Settings.UPLOADMODE.netcred:
                            // NET with credentials destination
                            // first, save to temp working folder
                            var tempCredXMLFilePath = Path.Combine(tempXMLPath, xmlFileName);
                            XMLOut.Save(tempCredXMLFilePath);

                            // upload network access with credentials
                            var finalNetPath = @"\\" + Settings.NetCredComputerName + "\\" + Path.Combine(Settings.NetCredXMLSavePath, Playlist.ChannelNumber, Path.GetFileName(xmlFileName));
                            if (!UploadWithCreds(tempCredXMLFilePath, finalNetPath, ref crcNotUsed)) return false;
                                
                            break;
                    }

                }

                XMLOut = null;

                return true;
            }
            catch (Exception ex)
            {
                UpdateStatusFail("SavePlaylistToXML (ERROR): " + ex.Message);
                return false;
            }
        }

        private bool AddSongToXML(cSongX song, PBXML XMLOut)// string destFilePath)
        {
            var uncFinalPath = string.Empty;
            string uncDirAndFile;
            string extension;

            try
            {
                PBItem pbitem;
                pbitem.id = song.SongID;
                pbitem.type = "video_clip";

                if (Settings.EnableEncryption)
                {
                    extension = FILE_EXTENSION_ENC;
                }
                else
                {
                    extension = FILE_EXTENSION_MP2;
                }

                uncDirAndFile = Path.Combine(GetDestFolderName(song.PublishedSongID + extension), song.PublishedSongID + extension);

                switch (Settings.UploadMode)
                {
                    case Settings.UPLOADMODE.ftp:
                        // transform ftp path into unc path for xml output file.
                        uncFinalPath = @"\\" + Settings.AthensaIP + Path.Combine(Settings.FTPContentRoot.Replace('/', '\\'), uncDirAndFile);

                        break;

                    case Settings.UPLOADMODE.unc:

                        uncFinalPath = Path.Combine(Settings.AthensaIP, uncDirAndFile);

                        break;

                    case Settings.UPLOADMODE.netcred:

                        uncFinalPath = @"\\" + Path.Combine(Settings.AthensaIP, Settings.NetCredContentSavePath, uncDirAndFile);

						break;
                }

                pbitem.file = uncFinalPath; // file path to final encoded content

                // convert to seconds and format to 4 decimal places (padded)
                float duration = (song.EndPosition - song.StartPosition - song.Segue) / 1000;
                var sduration = duration.ToString("0.0000");

                pbitem.outp = sduration;
                pbitem.duration = sduration;

                pbitem.isdynamicmedia = "false";

                var fileNameWoExt = Path.GetFileNameWithoutExtension(song.OriginalFile);
                pbitem.title = song.SongTitle + " by " + song.Artist + " (" + fileNameWoExt + ")";

                // FIXED_START_TIME not currently used
                PBFixedStartTime pbfixed;
                pbfixed.IncludeInXML = false; // indicates to not add FIXED_START_TIME to xml output
                pbfixed.active = "";
                pbfixed.time = "";
                pbfixed.early_tolerance = "";
                pbfixed.later_tolerance = "";
                pbfixed.overlap_resolving = "";
                pbfixed.day_offset = "";
                pbfixed.fill_category = "";

                // METADATA name value pairs
                List<PBItemMetadata> pbmeta = new List<PBItemMetadata>
                {
                    new PBItemMetadata { name = "clip_title", data = song.SongTitle.Truncate(MAX_METADATA_LENGTH) },
                    new PBItemMetadata { name = "clip_artist", data = song.Artist.Truncate(MAX_METADATA_LENGTH) },
                    new PBItemMetadata { name = "clip_album", data = song.AlbumTitle.Truncate(MAX_METADATA_LENGTH) },
                    new PBItemMetadata { name = "clip_label", data = song.LabelName.Truncate(MAX_METADATA_LENGTH) },
                    new PBItemMetadata { name = "clip_composer", data = song.Composers.Truncate(MAX_METADATA_LENGTH) },
                    new PBItemMetadata { name = "clip_category", data = song.Genre.Truncate(MAX_METADATA_LENGTH) },
                    new PBItemMetadata { name = "zone_id", data = this.Playlist.ZoneID }
                };

                XMLOut.AddItem(pbitem, pbfixed, pbmeta);

                return true;
            }
            catch (Exception ex)
            {
                UpdateStatusFail("SavePlaylistToXML (ERROR): " + ex.Message);
                return false;
            }
        }

        private bool EnsureDestinationFolder(string fileName)
        {
            var destFolder = GetDestFolderName(fileName);
            var destPath = Path.Combine(Settings.PathDestinationRoot, destFolder);

            return CreateDirectory(destPath);
        }

        private bool CopyContentToDestination(string fileName, ref string crc, ref string destFilePath)
        {
            bool result;

            var destFolder = GetDestFolderName(fileName);

            var destPath = Path.Combine(Settings.PathDestinationRoot, destFolder);
            var filePath = Path.Combine(this.PathTempFolder, fileName);
            
            // set final destination path, which is also returned byref
            destFilePath = Path.Combine(destPath, fileName);

            result = CopyFile(filePath, destFilePath, ref crc);

            // cleanup (delete) local temp content file
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch { }

            return result;
        }

        private string GetDestFolderName(string fileName)
        {
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            var filenameValue = int.Parse(fileNameWithoutExtension);

            // folders each contain up to 1000 files.
            // first folder is zero e.g. {0000, 0001, 0002, ...}
            string destFolder = "0000";
            // needed to add float.parse because integer math does not work for this.
            if (1 < (float.Parse(filenameValue.ToString()) / 1000))
            {
                destFolder = ((filenameValue - (filenameValue % 1000)) / 1000).ToString().PadLeft(4, '0');
            }

            return destFolder;
        }

        private bool RenameToFinal(cSongX song, ref string fileName)
        {
            // rename the final encoded WAV to use the published song id
            var encodedFilePath = Path.Combine(this.PathTempFolder, fileName);
            fileName = song.PublishedSongID + FILE_EXTENSION_MP2;
            var finalFilePath = Path.Combine(this.PathTempFolder, fileName);

            LogEntry("Renaming to Final MP2: " + fileName);
            return RenameFile(encodedFilePath, finalFilePath);
        }

        private bool EncryptAndRenameToFinal(cSongX song, ref string fileName)
        {
            // rename the final encoded WAV to use the published song id
            var encodedFilePath = Path.Combine(this.PathTempFolder, fileName);
            fileName = song.PublishedSongID + FILE_EXTENSION_ENC;
            var finalFilePath = Path.Combine(this.PathTempFolder, fileName);

            LogEntry("Encrypting to Final MP2: " + fileName);

            var sadEncryption = new SADEncryption();

            if (sadEncryption.Encrypt(encodedFilePath, finalFilePath))
            {
                File.Delete(encodedFilePath);
                return true;
            }

            return false;
        }

        private bool CreateDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch (Exception ex)
            {
                UpdateStatusFail("CreateDirectory (ERROR): " + ex.Message);
                return false;
            }

            return true;
        }

        private bool RenameFile(string fileName, string newFileName)
        {
            Exception exp = null;
            var success = false;

            // if not successful, up to 3 retries with 1 second interval in betweeen
            // this is just in case file is not free right away for some odd reason.
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    File.Move(fileName, newFileName);
                    success = true;
                    break;
                }
                catch (Exception ex)
                {
                    Thread.Sleep(1000);
                    exp = ex;
                    success = false;
                }
            }

            if (!success)
            {
                UpdateStatusFail("RenameFile (ERROR): " + exp.Message);
            }

            return success;
        }

        /// copies file and verifies copy using crc
        private bool CopyFile(string sourceFilePath, string destFilePath, ref string crc)
        {
            Exception exp = null;
            var success = false;

            try
            { 
                // get crc of source file
                crc = CRC.crcFile(sourceFilePath).ToString("X").ToLower();
            }
            catch (Exception ex)
            {
                UpdateStatusFail("CopyFile CRC (ERROR): " + ex.Message);
                return false;
            }

            // if not successful, up to 3 retries with 1 second interval in betweeen
            // this is just in case file is not free right away for some odd reason.
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (File.Exists(destFilePath))
                    {
                        File.Delete(destFilePath);
                    }

                    File.Copy(sourceFilePath, destFilePath); 
                    success = true;    
                    break;
                }
                catch (Exception ex)
                {
                    Thread.Sleep(1000);
                    exp = ex;
                    success = false;
                }
            }

            if (success)
            { 
                try
                {
                    // since file copy was successful, validate new crc to original
                    if (crc != CRC.crcFile(destFilePath).ToString("X").ToLower())
                    {
                        UpdateStatusFail("CopyFile CRC of source file did not match CRC of destination: " + exp.Message);
                        success = false;
                    }
                }
                catch
                {
                    UpdateStatusFail("CopyFile destination CRC (ERROR): " + exp.Message);
                    success = false;
                }                
            }
            else
            {
                UpdateStatusFail("CopyFile (ERROR): " + exp.Message);
            }

            return success;
        }

        private void CleanUp()
        {
            MP2 = null;

            if (null != this.ftpClient)
            {
                if (this.ftpClient.IsConnected)
                {
                    this.ftpClient.Disconnect();
                }

                this.ftpClient = null;
            }

            DeleteTempFiles();
        }

        private bool MP2Encode(cSongX song, int gainSetting ,ref string fileName)
        {
            var inFilePath = Path.Combine(this.PathTempFolder, fileName);
            var outFilePath = Path.Combine(this.PathTempFolder, Path.GetFileNameWithoutExtension(fileName) + ".mp2");

            MP2.OriginalTrackLength = song.OriginalTrackLength;
            MP2.StartPosition = song.StartPosition;
            MP2.EndPosition = song.EndPosition;
            MP2.FadeInLength = song.FadeInLength;
            MP2.FadeOutLength = song.FadeOutLength;
            MP2.NormalizeVolume = (!song.ExcludeGainLevel);

            try
            {
                LogEntry("MP2 Encoding:" + fileName);
                if (!MP2.QuickConvert(inFilePath, outFilePath, gainSetting))
                {
                    UpdateStatusFail("MP2 Encode Fail: " + fileName);
                    return false;
                }

                if (File.Exists(outFilePath))
                {
                    if (File.Exists(inFilePath))
                    {
                        File.Delete(inFilePath);
                        fileName = Path.GetFileName(outFilePath);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                UpdateStatusFail("MP2 Encode (ERROR): " + fileName + " : " + ex.Message);
                return false;
            }
        }

        // copy source file to local temp directory
        private bool GetSourceFile(cSongX song, out string fileName)
        {
            var uri = new Uri(song.DefaultLink.Href);
            var UNCPath = uri.LocalPath;
            int maxCopyAttempts = 3; // number of times copy will be attempted

            fileName = Path.GetFileName(UNCPath);
            string fileLocalPath = Path.Combine(this.PathTempFolder, fileName);

            if (!Directory.Exists(this.PathTempFolder))
            {
                Directory.CreateDirectory(this.PathTempFolder);
            }

            for (int copyAttemptNum = 1; copyAttemptNum <= maxCopyAttempts; copyAttemptNum++)
            {
                if (Settings.PrioritySourceServerEnable)
                {
                    string PriorityUNCPath = string.Empty;

                    try
                    {
                        if (string.IsNullOrWhiteSpace(Settings.PrioritySourceServerPath))
                        {
                            LogEntry(string.Concat("Skipping PrioritySourceServer download; UsePrioritySourceServer=True in App.Config, but NO PrioritySourceServer value set. (", copyAttemptNum.ToString(), ")"));
                        }
                        else
                        {
                            PriorityUNCPath = Path.Combine(Settings.PrioritySourceServerPath, Path.GetFileName(UNCPath));

                            LogEntry(string.Concat("Downloading source file from PrioritySourceServer attempt(", copyAttemptNum.ToString(), ") <", PriorityUNCPath, "> "));
                            File.Copy(PriorityUNCPath, fileLocalPath, true);

                            if (this.Playlist.PlaylistClient.SHA1Validation(fileLocalPath, song, this.PlaylistQueueReferenceID, false))
                            {
                                LogEntry(string.Concat("File Downloaded From <", PriorityUNCPath, "> "));

                                if (File.Exists(fileLocalPath))
                                {
                                    try
                                    {
                                        var info = new FileInfo(fileLocalPath);
                                        song.OriginalFileSizeInBytes = info.Length;
                                    }
                                    catch (Exception ex)
                                    {
                                        UpdateStatusFail("Could not get size of local source file (ERROR) (" + fileLocalPath + ") " + ex.Message);
                                        return false;
                                    }
                                }

                                return true;
                            }
                            else
                            {
                                LogEntry(string.Concat("Failed SHA1CRC for PrioritySourceServer file (", copyAttemptNum.ToString(), ") <", PriorityUNCPath, "> "));
                                LogEntry("Will now attempt download from alternate server.");
                                File.Delete(fileLocalPath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogEntry(string.Concat("Failed to download source file from PrioritySourceServer attempt(", copyAttemptNum.ToString(), ") <", PriorityUNCPath, "> ", ex.Message));
                        LogEntry(string.Concat("Will now attempt download from alternate server."));
                    }
                }

                try
                {
                    // AWS S3 source
                    if (uri.Scheme.Contains("http"))
                    {
                        string S3Bucket;
                        string S3BucketPath;

                        int afterSecondSlashIndex;
                        int firstDotIndex;
                        int lengthOfBucketName;

                        Logger.Log(String.Concat($"Attempting download from Amazon S3 ({uri.AbsoluteUri})"), LogTypeFlags.Trace, TraceLevel.Info, DisplayAsType.Information);

                        afterSecondSlashIndex = uri.OriginalString.IndexOf("//") + 2;
                        firstDotIndex = uri.OriginalString.IndexOf(".");
                        lengthOfBucketName = firstDotIndex - afterSecondSlashIndex;

                        S3Bucket = uri.OriginalString.Substring(afterSecondSlashIndex, lengthOfBucketName);
                        S3BucketPath = uri.AbsolutePath.Substring(1);

                        S3Download(Settings.AmazonS3AccessKey, Settings.AmazonS3SecretKey, S3Bucket, S3BucketPath, fileLocalPath);


                        if (this.Playlist.PlaylistClient.SHA1Validation(fileLocalPath, song, this.PlaylistQueueReferenceID, false))
                        {
                            LogEntry(string.Concat("File Downloaded From <", uri.AbsoluteUri, "> "));

                            if (File.Exists(fileLocalPath))
                            {
                                try
                                {
                                    var info = new FileInfo(fileLocalPath);
                                    song.OriginalFileSizeInBytes = info.Length;
                                }
                                catch (Exception ex)
                                {
                                    UpdateStatusFail("Could not get size of local source file (ERROR) (" + fileLocalPath + ") " + ex.Message);
                                    return false;
                                }
                            }

                            return true;
                        }
                        else
                        {
                            LogEntry(string.Concat("Failed SHA1CRC for S3 file (", copyAttemptNum.ToString(), ") <", uri.AbsoluteUri, "> "));
                            File.Delete(fileLocalPath);
                        }

                        continue;
                    }
                }
                catch (Exception ex)
                {
                    if (copyAttemptNum == maxCopyAttempts)
                    {
                        UpdateStatusFail("Source file failed to download: " + uri.AbsoluteUri + " : " + ex.Message);
                        return false;
                    }
                    else
                    {
                        LogEntry(ex.Message);
                        LogEntry(string.Concat("Waiting for next download attempt <", uri.AbsoluteUri, "> ..."));
                        System.Threading.Thread.Sleep(10000); // wait 10 seconds before next attempt.   
                    }
                    continue;
                }

                try
                {
                    LogEntry(string.Concat("Downloading source file attempt(", copyAttemptNum.ToString(), ") <", UNCPath, "> "));
                    File.Copy(UNCPath, fileLocalPath, true);

                    if (this.Playlist.PlaylistClient.SHA1Validation(fileLocalPath, song, this.PlaylistQueueReferenceID, false))
                    {
                        LogEntry(string.Concat("File Downloaded From <", UNCPath, "> "));
                        if (File.Exists(fileLocalPath))
                        {
                            try
                            {
                                var info = new FileInfo(fileLocalPath);
                                song.OriginalFileSizeInBytes = info.Length;
                            }
                            catch (Exception ex)
                            {
                                UpdateStatusFail("Could not get size of local source file (ERROR) (" + fileLocalPath + ") " + ex.Message);
                                return false;
                            }
                        }

                        return true;
                    }
                    else
                    {
                        LogEntry(string.Concat("Failed SHA1CRC for PrioritySourceServer file (", copyAttemptNum.ToString(), ") <", UNCPath, "> "));
                        File.Delete(fileLocalPath);
                    }
                }
                catch (Exception ex)
                {
                    if (copyAttemptNum == maxCopyAttempts)
                    {
                        UpdateStatusFail("Source file failed to download: " + UNCPath + " : " + ex.Message);
                        return false;
                    }
                    else
                    {
                        LogEntry(ex.Message);
                        LogEntry(string.Concat("Waiting for next download attempt <", UNCPath, "> ..."));
                        System.Threading.Thread.Sleep(10000); // wait 10 seconds before next attempt.   
                    }
                }
            }

            if (!File.Exists(fileLocalPath))
            {
                UpdateStatusFail("Source file failed to download (" + song.DefaultLink.Href + ")");
            }

            return false; // was not able to download file.
        }

        private void DeleteTempFolder()
        {
            try 
	        {	     
                LogEntry("Deleting Temp Folder");
                if (Directory.Exists(this.PathTempFolder))
                {
                    Directory.Delete(this.PathTempFolder, true);
                }
	        }
	        catch (Exception ex)
	        {
                LogEntry("Error Deleting Temp Folder: " + ex.Message);
	        }
        }

        protected void FTPConnect()
        {
            try
            {
                if (this.ftpClient != null)
                {
                    if (this.ftpClient.IsConnected)
                    {
                        return; // already connected.
                    }
                }

                switch (Settings.FTPProtocol.ToUpper())
                {
                    case "FTP":
                        this.ftpClient = new SyncMule(EnterpriseDT.Net.Ftp.FileTransferProtocol.FTP);
                        break;
                    case "SFTP":
                        this.ftpClient = new SyncMule(EnterpriseDT.Net.Ftp.FileTransferProtocol.SFTP);
                        break;
                }

                // SyncMule.KeepAliveIdle = true
                // SyncMule.KeepAlivePeriodSecs = inseconds

                ftpClient.Connect(Settings.FTPIP, Settings.FTPUserName, Settings.FTPPassword, Settings.FTPPort, Settings.FTPPassive);
            }
            catch (Exception ex)
            {
                throw new Exception("FTPConnection - IP Address: " + Settings.FTPIP + ", Port: " + Settings.FTPPort.ToString() + ", User: " + Settings.FTPUserName + ", Password: " + Settings.FTPPassword + ", Passive: " + Settings.FTPPassive.ToString() + ", Reason: " + ex.Message);
            }

            if (!ftpClient.IsConnected)
                throw new Exception("FTPConnection - IP Address: " + Settings.FTPIP + ", Port: " + Settings.FTPPort.ToString() + ", User: " + Settings.FTPUserName + ", Password: " + Settings.FTPPassword + ", Passive: " + Settings.FTPPassive.ToString());
        }

        protected void TransferFileToFTP(string fileName, string localPath, string FTPPath)
        {
            string source = Path.Combine(localPath, fileName);
            string tempRemote = FTPPath + fileName + "_";
            string finalRemote = FTPPath + fileName;

            try
            {
                if (!ftpClient.DoesDirExist(FTPPath))
                {
                    ftpClient.MakeDir(FTPPath);

                    if (!ftpClient.DoesDirExist(FTPPath))
                    {
                        ftpClient.MakeDir(FTPPath);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Failed FTP: could not create remote directory :" + source + ", To: " + tempRemote + ", Exception: " + ex.Message);
            }

            try
            {
                ftpClient.UploadFile(source, tempRemote);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed FTP upload - From :" + source + ", To: " + tempRemote + ", Exception: " + ex.Message);
            }

            if (!FTPCRCorFileSizeCheck(source, tempRemote))
            {
                // delete remote file and then try upload one more time.
                if (ftpClient.DoesFileExist(tempRemote))
                {
                    ftpClient.DeleteFile(tempRemote);
                }

                // second upload try
                try
                {
                    ftpClient.UploadFile(source, tempRemote);
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed FTP upload - From :" + source + ", To: " + tempRemote + ", Exception: " + ex.Message);
                }

                // second crc check
                if (!FTPCRCorFileSizeCheck(source, tempRemote))
                {
                    throw new Exception("FailedCRCorFileSize check - IPAddress: " + Settings.FTPIP + ", From :" + source + ", To: " + tempRemote);
                }
            }

            try
            {
                if (ftpClient.DoesFileExist(finalRemote))
                {
                    ftpClient.DeleteFile(finalRemote);
                }

                ftpClient.RenameFile(tempRemote, finalRemote);
            }
            catch (Exception ex)
            {
                throw new Exception("FTP Rename File Error - IPAddress: " + Settings.FTPIP + ", From :" + tempRemote + ", To: " + FTPPath + fileName + "\n" + ex.Message + " " + ex.Source);
            }
        }

        protected bool FTPCRCorFileSizeCheck(string localPath, string FTPPath)
        {
            bool valid = false;

            int localCRC;
            string FTPCRCHex = string.Empty;

            EnterpriseDT.Net.Ftp.FTPFile ftpFile = null;

            try
            {
                if (Settings.isEnableCRC)
                {
                    if (File.Exists(localPath))
                    {
                        localCRC = CRC.crcFile(localPath);
                    }
                    else
                    {
                        throw new Exception("CRC ERROR: LocalDirectoryOrFileDoesNotExist: " + localPath);
                    }

                    string tcpErrMsg = string.Empty;
                    try
                    {
                        FTPCRCHex = tcpClient.Net_GCRC_GetResponse(cTCPClient.CRCCommand.GCRCFile, FTPPath);
                    }
                    catch (Exception tcpexp)
                    {
                        tcpErrMsg = tcpexp.Message;
                        throw new Exception("TCPCRCFailed IPAddress: " + Settings.FTPIP + ", CRCPort: " + Settings.CRCPort.ToString() + ", File: " + FTPPath + ", CRC Error: " + tcpErrMsg);
                    }
                    finally
                    {
                        if (FTPCRCHex.Equals(string.Empty))
                        {
                            throw new Exception("TCPCRCFailed CRC returned empty string.  IPAddress: " + Settings.FTPIP + ", CRCPort: " + Settings.CRCPort.ToString() + ", File: " + FTPPath + ", CRC Error: " + tcpErrMsg);
                        }
                    }

                    valid = localCRC == Convert.ToInt32(FTPCRCHex, 16);
                }
                else
                {
                    if (!ftpClient.DoesFileExist(FTPPath, ref ftpFile))
                    {
                        throw new Exception("FTPDirectoryOrFileDoesNotExist IPAddress: " + Settings.FTPIP + ", File: " + FTPPath);
                    }

                    valid = ftpFile.Size == new FileInfo(localPath).Length;
                }
            }
            finally
            {
                if (!valid)
                {
                    ftpClient.DeleteFile(FTPPath);
                }

            }

            return valid;
        }

        private bool UploadWithCreds(string localFilePath, string finalNetPath, ref string crc)
        {

            Exception exp = null;
            bool success = false;

            try
            {
                // get crc of source file
                crc = CRC.crcFile(localFilePath).ToString("X").ToLower();
            }
            catch (Exception ex)
            {
                UpdateStatusFail("CopyFile CRC (ERROR): " + ex.Message);
                return false;
            }

            try
            {

                using (NetworkShareAccesser.Access(Settings.NetCredComputerName, Settings.NetCredDomain, Settings.NetCredUserName, Settings.NetCredPassword))
                {
                    var finalDirectory = Path.GetDirectoryName(finalNetPath);

                    if (!Directory.Exists(finalDirectory))
                    {
                        Directory.CreateDirectory(finalDirectory);
                    }

                    // if not successful, up to 3 retries with 1 second interval in betweeen
                    // this is just in case file is not free right away for some odd reason.
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            if (File.Exists(finalNetPath))
                            {
                                File.Delete(finalNetPath);
                            }

                            File.Copy(localFilePath, finalNetPath);

                            success = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            Thread.Sleep(1000);
                            exp = ex;
                            success = false;
                        }
                    }

                    try
                    {
                        // since file copy was successful, validate new crc to original
                        if (crc != CRC.crcFile(finalNetPath).ToString("X").ToLower())
                        {
                            UpdateStatusFail("CopyFile CRC of source file did not match CRC of destination: " + localFilePath);
                            success = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        UpdateStatusFail("CopyFile destination CRC (ERROR): " + ex.Message);
                        success = false;
                    }

                }
            }
            catch (Exception ex)
            {
                UpdateStatusFail("UploadWithCreds (ERROR): " + ex.Message);
                return false;
            }
            finally
            {
                // cleanup (delete) local temp content file
                try
                {
                    if (File.Exists(localFilePath))
                    {
                        File.Delete(localFilePath);
                    }
                }
                catch(Exception ex)
                {
                    Log.WriteEntry("Error while deleting local file: " + localFilePath + "\n " + ex.Message, DisplayAsType.Warning);
                }
            }

            if (!success)
            {
                UpdateStatusFail("CopyFile (ERROR): " + exp.Message);
            }

            return success;

        }

        internal void UpdateStatusFail(string StatusText)
        {
            if (!this.Playlist.PlaylistClient.UpdateStatusFail(this.PlaylistQueueReferenceID, StatusText))
            {
                this.Playlist.PlaylistClient.MovePlaylistXMLtoFailedStatusCallFolder(this.Playlist.Playlist, this.Playlist.XmlPath);
            }
        }

        // Amazon Web Services S3 File download
        private void S3Download(string accessKey, string secretKey, string bucket, string bucketPath, string localPath)
        {
            Log.WriteEntry($"Downloading File From Amazon S3 to temp: {localPath}", DisplayAsType.Information);

            using (var s3Client = new AmazonS3Client(accessKey, secretKey, Amazon.RegionEndpoint.USEast1))
            {
                var realPath = System.Net.WebUtility.HtmlDecode(bucketPath);
                var request = new GetObjectRequest { BucketName = bucket, Key = realPath}; 

                using (var response = s3Client.GetObject(request))
                using (var responseStream = response.ResponseStream)
                using (var FileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write))
                {
                    responseStream.CopyTo(FileStream);
                }
            }
        }

        private void LogEntry(string message)
        {
            if (!ServiceStopping())
            {
                Log.WriteEntry(message, DisplayAsType.Information);
            }
        }

        private bool ServiceStopping()
        {
            if (this.CancelToken.IsCancellationRequested)
            {
                if (!this.loggedServiceStopping)
                {
                    Log.WriteEntry("SADBSConveyor Service Stopping.", DisplayAsType.Information);
                    this.loggedServiceStopping = true;
                }
                return true;
            }
            else
            {
                return false;
            }
        }
    }

}
