using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SADBSConveyorLib;
using SADBSConveyorCSharpLib;
using System.IO;
using System.Diagnostics;
using Microsoft.VisualBasic;

namespace SADBSConveyor
{
    public class ConveyManager
    {
        Logging Log; // main process log
        CancellationToken CancelToken;

        private Data Data = new Data();

        public void Start(Logging log, CancellationToken cancelToken)
        {
            this.Log = log;
            this.CancelToken = cancelToken;
            
            InitData();

            UpdateServiceStatus("SADBSConveyor Starting");

            try
            {
                Process();
            }
            catch(Exception ex)
            {
                Log.WriteEntry("Critical Error:" + ex.Message + " : " + ex.Source + " : " + ex.StackTrace, DisplayAsType.Information);
            }
        }

        public void Process()
        {
            var tasks = new List<TaskContainer>();
            TaskContainer freeTask = null;

            Log.WriteEntry("ConveyManager Starting.", DisplayAsType.Information);

            Thread.Sleep(8000); // make sure service is "warmed" up.

            if (!Directory.Exists(Settings.PathPlaylistXml))
            {
                Log.WriteEntry("Creating local playlist XML root folder", DisplayAsType.Information);
                Directory.CreateDirectory(Settings.PathPlaylistXml);
            }

            // set up task containers now, this won't change unless conguration has changed and service is restarted.
            // each task number will have it's own associated log
            for (int i = 0; i < Settings.MaxNumberOfPlaylistTasks; i++)
            {
                TaskContainer tc = new TaskContainer();
                tc.TaskNumber = (i + 1);
                tc.Task = null;
                tc.Log = new Logging();
                tasks.Add(tc);

                var taskPathPlaylistXML = Path.Combine(Settings.PathPlaylistXml, (i + 1).ToString());
                if (!Directory.Exists(taskPathPlaylistXML))
                {
                    Log.WriteEntry("Creating local playlist XML folder: task" + taskPathPlaylistXML + ")", DisplayAsType.Information);
                    Directory.CreateDirectory(taskPathPlaylistXML);
                }
            }

            // main loopy loop
            for (;;)
            {

                // clear completed task containers
                foreach (var tc in tasks)
                {
                    if (null != tc.Task)
                    {
                        if (tc.Task.IsCompleted)
                        {
                            tc.Task.Dispose();
                            tc.Task = null;
                        }
                    }
                }

                freeTask = null;
                foreach (var tc in tasks)
                {
                    if (null == tc.Task)
                    {
                        freeTask = tc;
                        break;
                    }
                }

                if (null != freeTask) // a free task to use!
                {
                    try
                    {
                        Convey(freeTask);
                    }
                    catch (Exception ex)
                    {
                        Log.WriteEntry("Serious Error:" + ex.Message + " : " + ex.Source + " : " + ex.StackTrace, DisplayAsType.Information);  
                    }
                } 

                if (CancelToken.IsCancellationRequested) 
                {
                    foreach (var tc in tasks)
                    {
                        if (!tc.Task.IsFaulted)
                        {
                            tc.Task.Wait();
                        }
                        tc.Log = null;
                    }
                    break; // service is shutting down
                } 

                 Thread.Sleep(Settings.WaitTimeShortMilliseconds); // short pause - mostly for debugging?
            }

            UpdateServiceStatus("Service Shutting Down.");

        } // Start

        private void Convey(TaskContainer freeTask)
        {
            var validateMsg = string.Empty;

            // for now set the active log to the mainlog, 
            // but before handing playlist off to it's own task, give it a log that is associated with the task.
            // also, check to see if there is a local playlist xml in the current free task folder.
            var taskXmlPath = Path.Combine(Settings.PathPlaylistXml, freeTask.TaskNumber.ToString());
            var client = new PlaylistClient(Log, Settings.EndpointURL, taskXmlPath);

            Data.UpdateServiceStatus("SADBSConveyor: Checking for New Playlists to Process");

            cPlaylistX playlist = new cPlaylistX(client);
            playlist.Playlist = client.GetNextPlaylistFromXML(Settings.EndpointTargetPlatform.Split());

            validateMsg = string.Empty;
            if (null == playlist.Playlist) // no local playlist xml file found, so call endpoint
            {
                Log.WriteEntry("Calling Endpoint Dequeue.", DisplayAsType.Information);
                playlist.Playlist = client.GetNextPlaylist(Settings.EndpointTargetPlatform.Split());
                Log.WriteEntry("Endpoint Dequeue returned.", DisplayAsType.Information);

                validateMsg = playlist.PreValidate();
                if (string.Empty == validateMsg)
                {
                    // save playlist xml locally in case of Conveyor crash we will re-process
                    Log.WriteEntry("Saving Playlist xml locally", DisplayAsType.Information);
                    playlist.PlaylistClient.SavePlaylistToXML(playlist.Playlist, taskXmlPath);
                    Log.WriteEntry("Playlist xml saved", DisplayAsType.Information);
                }
                else
                {
                    Log.WriteEntry("No playlist; pausing for wait_long_seconds (" + (Settings.WaitTimeLongMilliseconds / 1000).ToString() + ") seconds...", DisplayAsType.Information);

                    // no playlists returned, set longer wait time until next try  
                    var stopWatch = new Stopwatch();
                    stopWatch.Start();
                    while (stopWatch.Elapsed < TimeSpan.FromMilliseconds(Settings.WaitTimeLongMilliseconds))
                    {
                        // stop processing in case of service shutdown
                        if (this.CancelToken.IsCancellationRequested)
                        {
                            stopWatch.Stop();
                            return;
                        }
                    }
                    stopWatch.Stop();
                }
            }

            // save another copy of playlist xml that is not deleted, for test / debugging purposes.
            if (null != Settings.TestPlaylistXMLPath && null != playlist.Playlist)
            {
                if (Settings.TestPlaylistXMLPath.Length > 0)
                {
                    Log.WriteEntry("TEST: Saving Playlist xml to TestPlaylistXMLPath(" + Settings.TestPlaylistXMLPath + ")", DisplayAsType.Information);
                    playlist.PlaylistClient.SavePlaylistToXML(playlist.Playlist, Settings.TestPlaylistXMLPath);
                    Log.WriteEntry("TEST: Playlist xml saved to TestPlaylistXMLPath(" + Settings.TestPlaylistXMLPath + ")", DisplayAsType.Information);
                }
            }

            playlist.XmlPath = taskXmlPath;

            if (string.Empty == validateMsg)
            {
                Log.WriteEntry("Playlist GUID (" + playlist.Playlist.Id.ToString() + ")", DisplayAsType.Information);
                Log.WriteEntry("Beginning playlist validation", DisplayAsType.Information);
                validateMsg = playlist.Validate();
            }

            if (string.Empty == validateMsg)
            {
                Log.WriteEntry("Playlist validation completed successfully", DisplayAsType.Information);

                //song validation and populationg to song collection
                Log.WriteEntry("Beginning songs validation", DisplayAsType.Information);
                validateMsg = playlist.objDistinctSongs.Populate();
                if (string.Empty == validateMsg)
                {
                    Log.WriteEntry("Songs validation completed successfully", DisplayAsType.Information);

                    Log.WriteEntry("Sending playlist to worker process(" + freeTask.TaskNumber.ToString() + ")", DisplayAsType.Information);
                    var worker = new ConveyWorker();
                    freeTask.Task = Task.Factory.StartNew(() => worker.Start(playlist, freeTask.Log, freeTask.TaskNumber, CancelToken));
                }
                else
                {
                    // songs validation fail message
                    Log.WriteEntry(validateMsg, DisplayAsType.Information);
                    // delete downloaded playlist xml
                    playlist.PlaylistClient.DeletePlaylistXML(playlist.Playlist, playlist.XmlPath);
                }
            }
            else
            {
                // playlist validation fail message
                Log.WriteEntry(validateMsg, DisplayAsType.Information);
                // delete downloaded playlist xml
                playlist.PlaylistClient.DeletePlaylistXML(playlist.Playlist, playlist.XmlPath);
            }
        }

        private void InitData()
        {
            this.Data.Server = Settings.DatabaseServer;
            this.Data.Database = Settings.DatabaseName;
            this.Data.UserName = Settings.DatabaseUserName;
            this.Data.Password = Settings.DatabasePassword;
        }

        public bool UpdateServiceStatus(string status)
        {
            try
            {
                Data.UpdateServiceStatus(status);
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteEntry("Error occured calling Update Service Status database: " + ex.Message, DisplayAsType.Information);
                return false;
            }
        }
    }
}
