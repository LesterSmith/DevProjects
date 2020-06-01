using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using BusinessObjects;
using DataHelpers;
using CodeCounter;
using System.Diagnostics;
namespace DevProjects
{
    public enum ProjectStatus
    {
        Exists = 0,
        Created = 1
    }

    /// <summary>
    /// This class adds new DevProjects and maintains ProjectSync Table
    /// The whole reason for the ProjectSync table is that different developers
    /// could create projects with the same project name but they are 
    /// unrelated, although this is unlikely unless in the case of default
    /// project names like ConsoleApplication1, etc.
    /// W/o a sync table we can't separate the effort in reports
    /// 
    /// NOTE: Also, serious corporate developers will us a source control
    /// system.  Further, serious developers should branch their projects
    /// instead of copying a local project to a different path to make change
    /// and if they need to make another copy to get proof of concept, etc.,
    /// they should branch from the branch, etc., that is best practice.
    /// If a yahoo, like we have all done, determines to do it the old bad
    /// way, then the convulution of the ProjectFiles table for their project
    /// is exactly what they deserved!
    /// </summary>
    public class MaintainProject
    {
        #region members
        private ProcessProject ProjProcessor { get; set; }
        private ProcessSolution SlnProcessor { get; set; }
        private int codeLines = 0;
        private int blankLines = 0;
        private int designerLines = 0;
        private int commentLines = 0;
        FileLineCounter CodeCounter;
        #endregion

        #region ..ctor

        #endregion

        #region public methods

        public void CheckForInsertingNewProjectPath(DevProjPath localProj, string fullPath) 
        {
            // NOTE: newProj, coming from FileAnalyzer has
            // Name, LocalDevPath, CurrApp, idDBEngine, CountLines, ProjFileExt
            try
            {
                localProj.Machine = Environment.MachineName;
                localProj.UserName = Environment.UserName;
                localProj.CreatedDate = DateTime.Now;
                string localGitUrl = GetGitURLFromPath(fullPath);
                //not sure why after git clone, the girUrl does not end with .git
                // here is why https://github.com/jonschlinkert/is-git-url/issues/8
                // the above link says that it does not have to, an obvious inconsistency
                // however, since we are using the girUrl in DevProjects and ProjectSync to 
                // separate the possibility of the same name for unrelated projects, keep it ending
                // with ".git" in the url, this does not affect git, only the two database tables
                if (!string.IsNullOrWhiteSpace(localGitUrl) && !localGitUrl.EndsWith(".git"))
                    localGitUrl += ".git";
                var hlpr = new DHMisc();

                // get all projects and projectSync rows by the same project name
                ProjectsAndSyncs devProjectsAndSyncs = hlpr.GetProjectsAndSyncByName(localProj.DevProjectName);

                // is local project in DevProjects table?
                var theProjectInDevProjects =
                    devProjectsAndSyncs.ProjectList.Find(
                        x => x.DevProjectName.Equals(localProj.DevProjectName) &&
                        x.Machine.Equals(localProj.Machine) &&
                        x.UserName.Equals(localProj.UserName) &&
                        x.DevProjectPath.Equals(localProj.DevProjectPath));

                if (theProjectInDevProjects != null)
                {
                    theProjectInDevProjects.CountLines = localProj.CountLines;
                    // local project is in DevProjects
                    // is gitUrl in local file path?
                    if (!string.IsNullOrWhiteSpace(localGitUrl))
                    {
                        // yes localGitUrl extant, is GitURL in the DevProjects row?
                        if (!string.IsNullOrWhiteSpace(theProjectInDevProjects.GitURL))
                        {
                            // possible that the local proj was put in gitHub and ProjSync 
                            // not yet updated
                            if (theProjectInDevProjects.GitURL.Equals(localGitUrl))
                            {
                                // project and sync rows good, just update the file
                                UpdateProjectFiles(theProjectInDevProjects, fullPath, hlpr);
                                return;
                            }
                            else
                            {
                                // no, update devprojects and projectsync with localgiturl and update the file table
                                theProjectInDevProjects.GitURL = localGitUrl;
                                _ = hlpr.InsertUpdateDevProject(theProjectInDevProjects);
                                var ps = devProjectsAndSyncs.ProjectSyncList.Find(x => x.ID == theProjectInDevProjects.SyncID);
                                ps.GitURL = localGitUrl;
                                _ = hlpr.UpdateProjectSyncWithGitURL(ps);

                                //NOTE: since we updated Devprojects with giturl, we need to
                                // update the files table
                                _ = hlpr.UpdateProjectFilesWithGitURL(localGitUrl, theProjectInDevProjects.DevProjectName, theProjectInDevProjects.SyncID);
                                // since the giturl originally matched the project name and now matches localgirurl we could
                                // have created duplicate copies of the same relativefilename in the ProjectFiles table
                                // this next line will remove all dupes and leave the last updated row
                                RemoveDuplicateProjectFiles(theProjectInDevProjects.DevProjectName, theProjectInDevProjects.SyncID, hlpr);
                                // now update the current file 
                                _ = UpdateProjectFiles(theProjectInDevProjects, fullPath, hlpr);
                                return;
                            }
                        }
                        else
                        {
                            _ = UpdateProjectFiles(theProjectInDevProjects, fullPath, hlpr);
                            return;
                        }
                    }
                    else
                    {
                        // we have no localGitUrl
                        if (string.IsNullOrWhiteSpace(theProjectInDevProjects.GitURL))
                            theProjectInDevProjects.GitURL = theProjectInDevProjects.DevProjectName;
                        _ = UpdateProjectFiles(theProjectInDevProjects, fullPath, hlpr);
                        return;
                    }
                }
                else
                {
                    // local project not in DevProjects
                    // is this local project in gitHub?
                    if (!string.IsNullOrWhiteSpace(localGitUrl))
                    {
                        // yes localproject is in github
                        // is there a ProjectSync that matches the localGitUrl
                        var ps = devProjectsAndSyncs.ProjectSyncList.Find(x => x.GitURL == localGitUrl);
                        if (ps != null)
                        {
                            // yes, we have a sync row that matches local project gitUrl, 
                            // insert local project into DevProjects
                            localProj.ID = Guid.NewGuid().ToString();
                            localProj.GitURL = localGitUrl;
                            localProj.SyncID = ps.ID;
                            _ = hlpr.InsertUpdateDevProject(localProj);
                            _ = UpdateProjectFiles(localProj, fullPath, hlpr);
                            return;
                        }
                        else
                        {
                            // insert local project in DevProjects with GitURL
                            // and insert a ProjectSync row with local gitUrl and
                            // set the ProjectSync ID = DevProjects ID
                            // set devProjects.GitURL = devProjects.DevProjectName
                            var id = Guid.NewGuid().ToString();
                            var date = DateTime.Now;
                            localProj.SyncID = id;
                            localProj.GitURL = localGitUrl;
                            _ = InsertNewDevProjectsRow(localProj, id, localGitUrl, hlpr);
                            _ = InsertNewProjectSyncRow(localProj, id, localGitUrl, hlpr);
                            _ = UpdateProjectFiles(localProj, fullPath, hlpr);
                            return;
                        }
                    }
                    else
                    {
                        // insert a new project and sync row with the gitUrl set to the projectname b/c 
                        // the local projct is not in github at least not yet
                        var id = Guid.NewGuid().ToString();
                        var date = DateTime.Now;
                        localProj.ID = id;
                        localProj.SyncID = id;
                        localProj.GitURL = localProj.DevProjectName;
                        InsertNewDevProjectsRow(localProj, id, localProj.DevProjectName, hlpr);
                        InsertNewProjectSyncRow(localProj, id, localProj.DevProjectName, hlpr);
                        UpdateProjectFiles(localProj, fullPath, hlpr);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        private int InsertNewDevProjectsRow(DevProjPath newProj, string id, string gitUrl, DHMisc hlpr)
        {
            var createdDate = DateTime.Now;
            newProj.ID = id;
            newProj.GitURL = gitUrl;
            newProj.SyncID = id;
            newProj.DatabaseProject = false;
            newProj.CreatedDate = createdDate;
            return hlpr.InsertUpdateDevProject(newProj);
        }

        /// <summary>
        /// Since a project file can be saved before we get the proper gitUrl in
        /// the DevProjects & ProjectSync tables, we can get multiple copies (at least 2)
        /// of the same relative filename for the same project. This method removes any 
        /// except the latest so we dont get duplicate reporting
        /// </summary>
        /// <param name="devProjectName"></param>
        /// <param name="syncID"></param>
        private void RemoveDuplicateProjectFiles(string devProjectName, string syncID, DHMisc hlpr)
        {
            var projectFiles = hlpr.GetDuplicateProjectFiles(devProjectName, syncID);
            string relFileName = string.Empty; // projectFiles[0].RelativeFileName;
            foreach (var item in projectFiles)
            {
                if (item.RelativeFileName == relFileName)
                {
                    _ = hlpr.DeleteDuplicateProjectFile(item.ID);
                    continue;
                }
                relFileName = item.RelativeFileName;
            }
        }

        private int InsertNewProjectSyncRow(DevProjPath newProj, string id, string gitUrl, DHMisc hlpr)
        {
            var ps = new ProjectSync
            {
                ID = id,
                DevProjectName = newProj.DevProjectName,
                DevProjectCount = 1,
                GitURL = gitUrl,
                CreatedDate = DateTime.Now
            };
            return hlpr.InsertProjectSyncObject(ps);
        }

        /// <summary>
        /// This method is to be used by WindowEvents so we can put a SyncID
        /// and GitUrl in the WindowEvent to try to tie the time charges to
        /// the correct DevProjectName.  All the Window event has is the 
        /// ProjectName, AppName, Machine, and Username.
        /// </summary>
        /// <param name="projName"></param>
        /// <param name ="appName"></param>
        /// <param name ="userName"></param>
        /// <param name ="machine"></param>
        /// <returns>ProjectSync object</returns>
        public string GetProjectSyncIDForProjectName(string projName, string appName)
        {
            var hlpr = new DHMisc();

            // get all projects by the same name
            ProjectsAndSyncs pas = hlpr.GetProjectsAndSyncByName(projName);

            // if we find a local project by user/machine we will assume that is the
            // project this user is working on, if the user/machine has two projects
            // by the same name, one for local use and one for collaboration, they are
            // asking for trouble and this code will assume the standalone project
            // b/c we don't have a path except the one that we may find with the following
            // statement so we have no way to straighten out their convoluted development process
            var projectInDevProjects =
                pas.ProjectList.Find(
                    x => x.DevProjectName.Equals(projName) &&
                    x.Machine.Equals(Environment.MachineName) &&
                    x.UserName.Equals(Environment.UserName) && 
                    x.IDEAppName == appName); 
           return projectInDevProjects != null ? projectInDevProjects.SyncID : null;
        }

        /// <summary>
        /// A git config file was saved, try to find the project directory
        /// so we can see if the project is in DevProjects, must find a known IDE project file
        /// Example filePath: C:\VS2019 Projects\MyProject\.git\config
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns>Tuple<devProjectName, Path, projectFieExtension></returns>
        public Tuple<string, string, string> GetProjectFromGitConfigSaved(string filePath, List<NotableFileExtension> notableExtensions)
        {
            try
            {
                if (!filePath.ToLower().EndsWith(@"\.git\config")) return null;
                // back up to base directory to find the project file
                var basePath = Path.GetDirectoryName(filePath);
                basePath = Path.GetDirectoryName(basePath);
                var name = Path.GetFileNameWithoutExtension(basePath);
                var ext = notableExtensions.Find(x => x.Extension == "config");
                if (ext == null) return null;

                List<string> directories = GetDirectories(basePath);
                directories.Insert(0, basePath);
                foreach (var dir in directories)
                {
                    Console.WriteLine(dir);
                    var tuple = CheckForProjectFileInDirectoryForExtension(dir, ext, name);
                    if (tuple != null) return tuple;
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return null;
            }
        }

        /// <summary>
        /// This method can determine if a project name exits
        /// </summary>
        /// <param name="path"></param>
        /// <param name="ext = NotableFileExtension object for a config file which has all proj file extensions"></param>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public Tuple<string, string, string> CheckForProjectFileInDirectoryForExtension(string path, NotableFileExtension ext, string fileName)
        {
            try
            {
                string[] extensions = ext.IDEProjectExtension.Split('|');
                for (int i = 0; i < extensions.Length; i++)
                {
                    string fullFileName = Path.Combine(path, $"{fileName}.{extensions[i]}");
                    if (File.Exists(fullFileName))
                    {
                        return Tuple.Create(fileName, path, extensions[i]);
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return null;
            }
        }

        public NotableFileExtension GetExtensionObject(List<NotableFileExtension> extensions, string ext)
        {
            return extensions.Find(x => x.Extension.ToLower() == ext);
        }

        public List<string> GetDirectories(string path, string searchPattern = "*",
            SearchOption searchOption = SearchOption.AllDirectories)
        {
            if (searchOption == SearchOption.TopDirectoryOnly)
                return Directory.GetDirectories(path, searchPattern).ToList();

            var directories = new List<string>(GetDirectories(path, searchPattern));

            for (var i = 0; i < directories.Count; i++)
            {
                // for DevTracker Purposes we are not interested in the following folders
                // they will not contain the project.xxproj file
                if (directories[i].Contains(".git") 
                    || directories[i].Contains(".vs") 
                    || directories[i].Contains("packages")) continue;
                directories.AddRange(GetDirectories(directories[i], searchPattern));
            }

            return directories;
        }

        private List<string> GetDirectories(string path, string searchPattern)
        {
            try
            {
                return Directory.GetDirectories(path, searchPattern).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        private void InsertUpdateProjetSync(DevProjPath newProj, DHMisc hlpr)
        {
            var ps = new ProjectSync
            {
                ID = newProj.SyncID,
                DevProjectName = newProj.DevProjectName,
                GitURL = newProj.GitURL,
                CreatedDate = DateTime.Now,
                DevProjectCount = 1
            };
            hlpr.InsertUpdateProjectSync(ps);
        }

        //TODO: if a dev file gets saved before we have a syncid and giturl and
        // then that devprojects row gets a syncid, somehow we need to be able to 
        // mark the original file  so that it can be updated...
        // e.g., if the syncid and giturl are not filled, but we know the projectname before
        // we save the file, we could put the projectname in the giturl and when we get the
        // giturl and thus the sycnid, we can update where giturl = devprojectname
        // Currently, a file must be saved in a known project path or we don't save it
        // in ProjectFiles, so theoritically we now know the projectname even if we don't
        // have girUrl yet   this area has still to be thought thru....
        public int UpdateProjectFiles(DevProjPath newProj, string fullPath, DHMisc hlpr)
        {
            if (newProj.CountLines)
                CountLines(fullPath);
            ProjectFiles pf = GetProjectFilesObject(newProj, fullPath);
            return hlpr.InsertUpdateProjectFiles(pf);
        }

        private ProjectFiles GetProjectFilesObject(DevProjPath newProj, string fullPath)
        {
            ProjectFiles item = new ProjectFiles
            {
                DevProjectName = newProj.DevProjectName,
                RelativeFileName = GetRelativeFileName(fullPath, newProj.DevProjectPath),
                SyncID = newProj.SyncID,
                GitURL = newProj.GitURL,
                CodeLines = codeLines,
                CommentLines = commentLines,
                BlankLines = blankLines,
                DesignerLines = designerLines,
                CreatedBy = Environment.UserName,
                LastUpdatedBy = Environment.UserName
            };
            return item;
        }

        private ProjectSync GetProjectSyncIfExtant(DevProjPath newProj, ProjectsAndSyncs pas, string gitUrl )
        {
            ProjectSync ps = pas.ProjectSyncList.Find(
                x => x.DevProjectName.Equals(newProj.DevProjectName) &&
                x.ID.Equals(newProj.SyncID) &&
                x.GitURL.Equals(gitUrl));
            return ps;
        }
        private void CountLines(string fullPath)
        {
            codeLines = blankLines = designerLines = commentLines = 0;
            CodeCounter = new FileLineCounter(fullPath);
            if (CodeCounter.Success)
            {
                codeLines = CodeCounter.numberLines;
                blankLines = CodeCounter.numberBlankLines;
                commentLines = CodeCounter.numberCommentsLines;
                designerLines = CodeCounter.numberLinesInDesignerFiles;
            }
        }

        /// <summary>
        /// Probably used once to update sln path which was not in
        /// the original DevProjects table
        /// 1. get list of DevProjects on this machine and user
        /// 2. loop thru projects
        /// 3. if there is no solution path in the project object, try to 
        ///    find the sln file and put its path in the project
        /// </summary>
        /// <param name="projectPath"></param>
        /// <param name="projectName"></param>
        public void UpdateSLNPathInDevProjects()
        {
            var hlpr = new DHMisc();
            List<DevProjPath> projects = hlpr.GetDevProjects(Environment.UserName, Environment.MachineName);
            foreach (var project in projects)
            {
                if (project.DatabaseProject)
                    continue;
                var slnPath = FindSLNFileFromProjectPath(project);
                if (!string.IsNullOrWhiteSpace(project.DevSLNPath) || string.IsNullOrWhiteSpace(slnPath))
                    continue;
                var pp = new DevProjPath
                {
                    ID = project.ID,
                    DevSLNPath = slnPath
                };
                var rows = hlpr.UpdateSLNPathInDevProject(pp);
            }
        }

        /// <summary>
        /// Probably one time usage
        /// </summary>
        public void PopulateSyncTableFromDevProjects()
        {
            var hlpr = new DHMisc();
            List<DevProjPath> projects = hlpr.GetDevProjects();
            //var ctr = 1;
            var currDate = DateTime.Now;
            foreach (var project in projects)
            {
                if (!project.DatabaseProject)
                {
                    if (!string.IsNullOrWhiteSpace(project.SyncID)) continue;

                    var projectSync = GetNewProjectSyncObject(project, currDate);

                    // gitUrl will be empty if the project is not in gitHub
                    // if so, put project name in gitUrl so it can be updated later
                    hlpr.InsertProjectSyncObject(projectSync);
                    project.SyncID = projectSync.ID;
                    project.GitURL = projectSync.GitURL;
                    hlpr.UpdateDevProjectsWithSyncIDAndGitURL(project);
                }
            }
        }

        /// <summary>
        /// This method finds the project name from the path of
        /// a Development file (cs, vb, etc.)
        /// </summary>
        /// <param name="fullPath"></param>
        /// <param name="extList"></param>
        /// <param name="ext"></param>
        /// <returns></returns>
        public Tuple<string, string, string> GetProjectFromDevFileSave(string fullPath, List<NotableFileExtension> extList, string ext)
        {
            var tmpPath = Path.GetDirectoryName(fullPath);
            var extObj = extList.Find(x => x.Extension.ToLower().Equals(ext));
            if (extObj == null) 
                return null;

            while (!string.IsNullOrWhiteSpace(tmpPath))
            {
                string[] exts = extObj.IDEProjectExtension.Split('|');
                foreach (var e in exts)
                {
                    string[] files = Directory.GetFiles(tmpPath, $"*.{e}");
                    if (files.Length > 0)
                        return Tuple.Create(Path.GetFileName(tmpPath), tmpPath, e);
                }
                tmpPath = Path.GetDirectoryName(tmpPath);
            }
            return null;
        }


        /// <summary>
        /// If a project is in github, its Url will be in the path
        /// it will be in the form ..\projname\.git\config
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public string GetGitURLFromPath(string fullPath)
        {
            var urlPath = fullPath;
            while (!string.IsNullOrWhiteSpace(urlPath))
            {
                string fileName = string.Empty;
                if (File.Exists(urlPath))
                    fileName = Path.GetFileNameWithoutExtension(urlPath);

                if (fileName.ToLower().Equals("config"))
                    return GetGitURLFromConfigFile(urlPath);
                // we are not in the directory where config file is
                if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(urlPath)))
                    return string.Empty;
                var tryPath = Path.Combine(Path.GetDirectoryName(urlPath), ".git\\config");
                if (File.Exists(tryPath))
                    return GetGitURLFromConfigFile(tryPath);
                urlPath = Path.GetDirectoryName(urlPath);
            }
            return string.Empty;
        }

        /// <summary>
        /// we have the config file from git, return the url from it
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public string GetGitURLFromConfigFile(string fileName)
        {
            var patt = "(?<remote>\\[remote \"origin\"][\\r\\n|\\n|\\r])\\s*url\\s*=\\s*(?<url>.*?)[\r\n|\n|\r]";
            string config;
            using (StreamReader sr = new StreamReader(fileName))
            {
                config = sr.ReadToEnd();
                sr.Close();
            }
            var re = Regex.Match(config, patt);
            return re.Success && !string.IsNullOrWhiteSpace(re.Groups["url"].Value) ? re.Groups["url"].Value : string.Empty;
        }
        public string FindSLNFileFromProjectPath(DevProjPath project)
        {
            string projPath = project.DevProjectPath;
            while (!string.IsNullOrWhiteSpace(projPath))
            { 
                string slnName = $"{project.DevProjectName}.sln";
                string slnPath = Path.Combine(projPath, slnName);
                // sln normally in project path if it exists
                if (File.Exists(slnPath))
                    return Path.GetDirectoryName(slnPath);
                // otherwise if we are to find it, it should be one or more dirs back
                projPath = Path.GetDirectoryName(projPath);
            }
            return string.Empty;
        }
        private ProjectSync GetNewProjectSyncObject(DevProjPath project, DateTime currDate)
         {
            var syncID = Guid.NewGuid().ToString();
            //var projID = Guid.NewGuid().ToString();

            // get sln name if extant to get the nbr of projects in the sln
            //var slnName = !string.IsNullOrWhiteSpace(project.DevSLNPath) ? 
            //    Path.Combine(project.DevSLNPath, $"{project.DevProjectName}.{project.ProjFileExt}") : 
            //    string.Empty;
            //int slnProjects = File.Exists(slnName) ?
            //    new ProcessSolution(slnName).ProjectList.Count : 0;

            // get count of files in the project file if it has been saved
            string projFileName = Path.Combine(project.DevProjectPath, $"{project.DevProjectName}.{project.ProjFileExt}");
            //int projFileCount = File.Exists(projFileName) ?
            //    new ProcessProject(projFileName).FileList.Count : 0;
            var url = GetGitURLFromPath(projFileName);
            var projectSync = new ProjectSync
            {
                ID = syncID,
                DevProjectName = project.DevProjectName,
                CreatedDate = currDate,
                GitURL = !string.IsNullOrWhiteSpace(project.GitURL) ? project.GitURL : !string.IsNullOrWhiteSpace(url) ? url : project.DevProjectName,
                DevProjectCount = 1
            };
            return projectSync;
        }

        /// <summary>
        /// Strip the path from the front of the file to get relative file like
        /// c:\vs2019\DevTracker\Classes\Class1.cs is the fullpath
        /// but Classes\Class1.cs is the relative filename
        /// we need the relative name b/c there are multiple files with the 
        /// same name in a project as long as they are in different sub paths
        /// </summary>
        /// <param name="fullPath"></param>
        /// <param name="devPath"></param>
        /// <returns>project relative filename</returns>
        private string GetRelativeFileName(string fullPath, string devPath)
        {
            string devProjectPath = !devPath.EndsWith(@"\") ? devPath + @"\" : devPath;
            string projectRelativePath = fullPath.ToLower().Replace(devProjectPath.ToLower(), string.Empty);
            return projectRelativePath;
        }
        public DevProjPath IsFileInADevProjectPath(string fullPath)
        {
            var hlpr = new DataHelpers.DHFileWatcher();
            return hlpr.IsFileInDevPrjPath(fullPath);
        }
    }
    #endregion
}
