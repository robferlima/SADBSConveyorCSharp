using System;
using System.Linq;
using System.Data;
using System.Data.SqlClient;
using DMXCommon;
using SADBSConveyorLib;
using SADBSConveyorCSharpLib;

namespace SADBSConveyor
{
    class Data
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }

        public string ConnectionString
        {
            get 
            { 
                var conB = new SqlConnectionStringBuilder();              
                conB.DataSource = Server;
                conB.InitialCatalog = Database;
                conB.UserID = UserName;
                conB.Password = Password;

                return conB.ConnectionString; 
            }
        }

        private void CheckLastError(cSQLDataAccess DB)
        {
            if (DB.LastException.Message != null)
            {
                throw new Exception(DB.LastException.ToString());
            }
        }

        public string GetNextPublishedSongID()
        {
            string songNumber = string.Empty;
            using (var DB = new cSQLDataAccess())
            {
                DB.Connection.ConnectionString = this.ConnectionString;

                DB.RunSPDataReader("spDBS_GetNewSongNumber");

                if (DB.DataReaderReturn.Read())
                {
                    songNumber = DB.DataReaderReturn["SongNumber"].ToString().Trim();
                }

                CheckLastError(DB);
            }

            return songNumber;
        }

        public void GetDefaultSettingsPaths(ref string uncRootContentPath, ref string uncRootXMLOutPath)
        {
            using (var DB = new cSQLDataAccess())
            {
                DB.Connection.ConnectionString = this.ConnectionString;

                DB.RunSPDataReader("spDBS_SelDefaultSettingsPaths");

                if (DB.DataReaderReturn.Read())
                {
                    uncRootContentPath = DB.DataReaderReturn["UNCRootContentPath"].ToString();
                    uncRootXMLOutPath = DB.DataReaderReturn["UNCRootXMLOutPath"].ToString();
                }

                CheckLastError(DB);
            }
        }

        public void GetChannel(string programID, int sourceSystemID, ref int idChannel, ref string channelNumber, ref string zoneID)
        {
            using (var DB = new cSQLDataAccess())
            {
                DB.Connection.ConnectionString = this.ConnectionString;

                DB.AddParameter("@ProgramID", SqlDbType.VarChar, programID, ParameterDirection.Input);
                DB.AddParameter("@IDSource", SqlDbType.Int, sourceSystemID, ParameterDirection.Input);
                DB.RunSPDataReader("spDBS_SelChannel");

                if (DB.DataReaderReturn.Read())
                {
                    idChannel = int.Parse(DB.DataReaderReturn["IDMultiChoiceChannel"].ToString());
                    channelNumber = DB.DataReaderReturn["ChannelNumber"].ToString();
                    zoneID = DB.DataReaderReturn["ZoneID"].ToString();
                }
                else
                {
                    throw new Exception("GetChannel: could not get IDChannel and/or ChannelNumber from Database");
                }

                CheckLastError(DB);
            }
        }

        public bool FindSongMatch(cSongX song)
        {
            bool foundMatch = false;
            using (var DB = new cSQLDataAccess())
            {
                DB.Connection.ConnectionString = this.ConnectionString;

                DB.AddParameter("@SourceFileName", SqlDbType.VarChar, song.SongID, ParameterDirection.Input);
                DB.AddParameter("@SourceFileSHA1", SqlDbType.VarChar, song.DefaultLink.Checksum, ParameterDirection.Input);

                DB.AddParameter("@SongStartTime", SqlDbType.Int, song.SongEntry.StartTime, ParameterDirection.Input);
                DB.AddParameter("@SongEndTime", SqlDbType.Int, song.SongEntry.EndTime, ParameterDirection.Input);
                DB.AddParameter("@FadeInLength", SqlDbType.Int, song.SongEntry.FadeInLength, ParameterDirection.Input);
                DB.AddParameter("@FadeoutLength", SqlDbType.Int, song.SongEntry.FadeOutLength, ParameterDirection.Input);
                DB.AddParameter("@Encrypted", SqlDbType.Bit, Settings.EnableEncryption, ParameterDirection.Input);

                DB.RunSPDataReader("spDBS_FindSongMatch");

                if (DB.DataReaderReturn.Read())
                {
                    // note: value is trim()'d because it's a char(7) in the db
                    //       and would have trialing spaces if not trim()'d
                    string publishedFilename = DB.DataReaderReturn["PublishedFilename"].ToString().Trim();
                    foundMatch = (string.Empty == publishedFilename ? false : true);
                    if (foundMatch)
                    {
                        song.PublishedSongID = publishedFilename;
                        song.PublishedFilepath = DB.DataReaderReturn["PublishedFilepath"].ToString();
                    }
                }

                CheckLastError(DB);
            }

            return foundMatch;
        }

        public void SavePlaylist(cPlaylistX playlist)
        {
            DateTime firstSongBegin = playlist.Playlist.DateTimeSequence.References.First().Begin;
            DateTime lastSongBegin = playlist.Playlist.DateTimeSequence.References.Last().Begin;

            using (var DB = new cSQLDataAccess())
            {
                DB.Connection.ConnectionString = this.ConnectionString;

                DB.AddParameter("@IDMultiChoiceChannel", SqlDbType.Int, playlist.IDChannel, ParameterDirection.Input);
                DB.AddParameter("@PlaylistName", SqlDbType.VarChar, playlist.Playlist.Name, ParameterDirection.Input);
                DB.AddParameter("@SourcePlaylistID", SqlDbType.VarChar, playlist.Playlist.PlaylistInstanceId, ParameterDirection.Input);
                DB.AddParameter("@SourcePlaylistGUID", SqlDbType.VarChar, playlist.Playlist.Id.ToString(), ParameterDirection.Input);

                DB.AddParameter("@StartDateTime", SqlDbType.DateTime, firstSongBegin, ParameterDirection.Input);
                DB.AddParameter("@EndDateTime", SqlDbType.DateTime, lastSongBegin, ParameterDirection.Input);

                DB.RunSPNonQuery("spDBS_InsPlaylist");

                CheckLastError(DB);
            }
        }

        public void SaveSong(cSongX song, int sourceSystemID)
        {
            using (var DB = new cSQLDataAccess())
            {
                DB.Connection.ConnectionString = this.ConnectionString;

                DB.AddParameter("@IDSource", SqlDbType.Int, sourceSystemID, ParameterDirection.Input);
                DB.AddParameter("@SourceFileName", SqlDbType.VarChar, song.SongID, ParameterDirection.Input);
                DB.AddParameter("@SourceFilePath", SqlDbType.VarChar, song.OriginalFile, ParameterDirection.Input);
                DB.AddParameter("@SourceFileSHA1", SqlDbType.VarChar, song.DefaultLink.Checksum, ParameterDirection.Input);

                DB.AddParameter("@SourceFileLength", SqlDbType.Decimal, song.OriginalFileSizeInBytes, ParameterDirection.Input);

                DB.AddParameter("@SongTitle", SqlDbType.VarChar, song.SongEntry.Title, ParameterDirection.Input);
                DB.AddParameter("@Artist", SqlDbType.VarChar, song.SongEntry.Artist, ParameterDirection.Input);
                DB.AddParameter("@AlbumTitle", SqlDbType.VarChar, song.SongEntry.Release, ParameterDirection.Input);
                DB.AddParameter("@Composers", SqlDbType.VarChar, song.SongEntry.Composer, ParameterDirection.Input);

                DB.AddParameter("@SongStartTime", SqlDbType.Int, song.SongEntry.StartTime, ParameterDirection.Input);
                DB.AddParameter("@SongEndTime", SqlDbType.Int, song.SongEntry.EndTime, ParameterDirection.Input);
                DB.AddParameter("@FadeInLength", SqlDbType.Int, song.SongEntry.FadeInLength, ParameterDirection.Input);
                DB.AddParameter("@FadeoutLength", SqlDbType.Int, song.SongEntry.FadeOutLength, ParameterDirection.Input);

                DB.AddParameter("@Encrypted", SqlDbType.Bit, Settings.EnableEncryption, ParameterDirection.Input);

                DB.AddParameter("@PublishedFilename", SqlDbType.VarChar, song.PublishedSongID, ParameterDirection.Input);
                DB.AddParameter("@PublishedFilepath", SqlDbType.VarChar, song.PublishedFilepath, ParameterDirection.Input);

                DB.AddParameter("@PublishedFileCRC", SqlDbType.Char, song.CRC, ParameterDirection.Input);
                DB.AddParameter("@PublishedFileSize", SqlDbType.VarChar, song.PublishedFileSizeInBytes, ParameterDirection.Input);

                DB.RunSPNonQuery("spDBS_InsSong");

                CheckLastError(DB);
            }
        }

        public void UpdateServiceStatus(string status)
        {
            using (var DB = new cSQLDataAccess())
            {
                DB.Connection.ConnectionString = this.ConnectionString;

                DB.AddParameter("@TypeService", SqlDbType.VarChar, "DBSCONVEY", ParameterDirection.Input);
                DB.AddParameter("@ServiceLogin", SqlDbType.VarChar, Settings.DatabaseUserName , ParameterDirection.Input);
                DB.AddParameter("@Status", SqlDbType.VarChar, status, ParameterDirection.Input);
                DB.AddParameter("@InstalledServer", SqlDbType.VarChar, System.Environment.MachineName, ParameterDirection.Input);

                DB.RunSPNonQuery("spDBS_InsUpdServiceStatus");

                CheckLastError(DB);
            }
        }
    }
}
