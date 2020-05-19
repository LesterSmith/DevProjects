using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using BusinessObjects;
using DataHelpers;
using CodeCounter;
namespace DevProjects
{
    public enum ProjectStatus
    {
        Exists = 0,
        Created = 1
    }
    /// <summary>
    /// This class adds new DevProjects and helps maintain ProjectSync Table
    /// The whole reason for the ProjectSync table is that different devs
    /// could create projects with the same project name but they are 
    /// unrelated, although this is unlikely unless in the case of default
    /// project names like ConsoleApplication1, ect.
    /// W/o a sync table we can't separate the effort in reports
    /// NOTE: the assummption is that the likelihood of multiple developers
    /// adding the same DevProjectName which is unrelated to previous 
    /// instances of the same project name, at the exact same time, is
    /// very unlikely and not worthy of trying some locking mechanism or
    /// doing all the logic in a stored proc.  
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
        /// <summary>
        /// If this is the first instance of the DevProjectName in
        /// DevProjects, add it to DevProjects and ProjectSync table
        /// and then InsertUpdate ProjectFiles table
        /// If the DevProjectName is already in DevProjects and there are 
        /// multiple instances in ProjectSync, link the new one to the one 
        /// with the matching gitURL.
        /// 1. Does a project exist for this file in DevProjects?
        ///     If not, does gitUrl exist?  If not, simply insert a row in DevProjects
        ///     and write a row to ProjectFiles w/o SyncId or girUrl, no ProjectSync row
        ///     (revisit contents of these two fields for later update purposes). 
        /// 2. If project exists, does ProjectSync row exist?  If it does, is there
        ///     a gitUrl in the path of the file, if so does the ProjectSync URL match 
        ///     gitUrl found in the local path, if so, simply update/insert ProjectFiles
        ///     row with line counts etc.  
        ///     If we have a local gitUrl and it does not match ProjectSync gitUrl, 
        ///     it means that we have a new project by the same name but it is not the project
        ///     that we have in DevProjects (having looked at all of them by the name) and
        ///     we should enter a new ProjectSync with the local gitURL and insert new row in
        ///     DevProjects, if there is no row for this user/machine.  if there is a row in
        ///     Devprojects for this user/machine, assume gitUrl is there and if no project 
        ///     sync record put one there.
        /// 3. If project does not exist, insert into DevProjects.  If local 
        ///    gitUrl exists, insert ProjectSync record, putting girUrl in it a
        ///    and putting SyncID and GitUrl in DevProjects
        ///    
        /// 4. Finally, anytime we insert ProjectSync, we have the probablity
        ///    that ProjectFiles rows exist for the project that do not have
        ///    SyncID and GitURL populated because the files were written before
        ///    the project was put into gitHub on the local machine and so 
        ///    we must be able to UpdateProjectFiles with syncID and gitURL 
        ///    and therein lies a problem or maybe not...think it thru on paper
        ///    For example a developer creates a Project in GitHub, clones it
        ///    down to his local machine, thereby creating a local repo with a
        ///    accessible gitURL.  Next, they create a project in VS and as the 
        ///    files are saved this class sees no DevProject row, no ProjectSync
        ///    row, and creates both, and then has a syncId and girUrl for the
        ///    ProjectFiles rows, everything is cool, 
        ///    
        ///     And if a second or subsequent devs clone the project, they have
        ///     everything they need, but.........
        ///    
        ///    Another developer creates a project by the same name but not from
        ///    gitHub b/c it is unrelated (not collaborated) and so this class calls
        ///    InsertUpdateProjectFiles.  Here comes the problem, the files will log to
        ///    ProjectFiles with empty syncID and gitUrl, and from my testing it appears
        ///    that continued activity with the same relativeFilename will 
        ///    write to an existing row for the same project name b/c syncID
        ///    if I add to the where clause (
        ///    where (syncid = @syncid and GitUrl = @giturl) 
        ///    or    (syncid is null and isnull(gitUrl, '') = '')
        ///    
        ///    The downside of this is that multiple projects with the
        ///    same name will accumulate the same RelativeFilename. The
        ///    positive side of this is that "Relative" Filenames will not
        ///    often be the same in the few projects (of consequence, i.e.,
        ///    not ConoleApplication1, WinFormsApplication1, etc.)
        ///    And if someone really needs to know file stats on one of these
        ///    projects, the files exist on that dev's machine.
        ///    
        ///    On the bright side no duplicate projects are allowed in the same
        ///    gitHub organization, and other organizations would by definition
        ///    have a different gitUrl:)  Therefore, for major projects, we
        ///    should have not many Duplicate Project Names.  We have gone to
        ///    a huge amount of design, redesign, code, recode just to try
        ///    to handle a problem that should be a rarity, but 
        ///    Murphy WAS an Optimist!!!!!
        ///     
        /// </summary>
        /// <param name="newProj"></param>
        public void CheckForInsertingNewProjectPath(DevProjPath newProj, string fullPath)
        {
            // finish setting up newProj with all needed data
            newProj.Machine = Environment.MachineName;
            newProj.UserName = Environment.UserName;

            string syncID = Guid.NewGuid().ToString();
            string gitUrl = GetGitURLFromPath(newProj.DevProjectPath);
            newProj.SyncID = syncID;
            newProj.GitURL = gitUrl;

            var hlpr = new DHMisc();

            // get all projects by the same name
            ProjectsAndSyncs pas = hlpr.GetProjectsAndSyncByName(newProj.DevProjectName);

            var projectInDevProjects = 
                pas.ProjectList.Find(
                    x => x.DevProjectName.Equals(newProj.DevProjectName) &&
                    x.Machine.Equals(newProj.Machine) &&
                    x.UserName.Equals(newProj.UserName) &&
                    x.DevProjectPath.Equals(newProj.DevProjectPath));

            //1. does project exist in DevProjects
            if (projectInDevProjects != null)
            {
                // yes, this local project is in devprojects table
                // is ProjectSync extant?
                string localSyncID = projectInDevProjects.SyncID;
                ProjectSync ps = GetProjectSyncIfExtant(newProj, pas, localSyncID, gitUrl);

                // is there a ProjectSync record, extant only if gitUrl extant
                if (ps != null)
                {
                    // yes, ProjectSync and DevProjects all set up, 
                    // get file stats and log to ProjectFiles
                    UpdateProjectFiles(newProj, fullPath, hlpr);
                }
                else // in DevProjects but no ProjectSync row
                {
                    // is local project in github
                    if (string.IsNullOrWhiteSpace(gitUrl))
                    {
                        // no, we can't create a sync row w/o gitUrl
                        UpdateProjectFiles(newProj, fullPath, hlpr);
                    }
                    else // this local project is in gitHub, no ProjectSync yet
                    {
                        // insert ProjectSync, update DevProjects with syncID and URL
                        // update ProjectFiles with file data
                        InsertUpdateProjetSync(newProj, hlpr);
                        hlpr.UpdateDevProjectsWithSyncIDAndGitURL(newProj);
                        UpdateProjectFiles(newProj, fullPath, hlpr);
                    }
                }
            }
            else // project does not exist in DevProjects
            {
                // does gitUrl exist?
                if (string.IsNullOrWhiteSpace(gitUrl))
                {
                    // gitUrl does not exist, just insert into DevProjects
                    // and update ProjectFiles since no gitUrl dont write
                    // project sync
                    newProj.SyncID = string.Empty;
                    newProj.GitURL = string.Empty;
                    hlpr.InsertUpdateDevProject(newProj);
                    UpdateProjectFiles(newProj, fullPath, hlpr);
                }
                else 
                {
                    // yes, gitUrl exists, we must insert into all three tables
                    // b/c here the project is not in DevProjects, therefore no
                    // ProjectSync row, and must insert/update ProjectFiles
                    InsertUpdateProjetSync(newProj, hlpr);
                    hlpr.InsertUpdateDevProject(newProj);
                    UpdateProjectFiles(newProj, fullPath, hlpr);
                }
            }
        }


        //TODO: add SyncID to WindowEvents Table
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
            var projectInDevProjects =
                pas.ProjectList.Find(
                    x => x.DevProjectName.Equals(projName) &&
                    x.Machine.Equals(Environment.MachineName) &&
                    x.UserName.Equals(Environment.UserName)); // &&
                                                  //x.DevProjectPath.Equals(newProj.DevProjectPath));
            return projectInDevProjects != null ? projectInDevProjects.SyncID : null;
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

        private void UpdateProjectFiles(DevProjPath newProj, string fullPath, DHMisc hlpr)
        {
            if (newProj.CountLines)
                CountLines(fullPath);
            ProjectFiles pf = GetProjectFilesObject(newProj, fullPath);
            var rows = hlpr.InsertUpdateProjectFiles(pf);
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

        private ProjectSync GetProjectSyncIfExtant(DevProjPath newProj, ProjectsAndSyncs pas, string syncID, string gitUrl )
        {
            ProjectSync ps = pas.ProjectSyncList.Find(
                x => x.DevProjectName.Equals(newProj.DevProjectName) &&
                x.ID.Equals(syncID) &&
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

        //public SyncID GetMostUsedSyncID(List<DevProjPath> projList)
        //{
        //    List<SyncID> sids = new List<SyncID>();
        //    sids.Add(new SyncID { SID = projList[0].SyncID, Count = 0 });

        //    string lastSid = projList[0].SyncID;
        //    int ctr = 0;
        //    foreach (var devPP in projList)
        //    {
        //        if (devPP.SyncID.Equals(lastSid))
        //        {
        //            sids[ctr].Count++;
        //            continue;
        //        }

        //        ctr++;
        //        lastSid = devPP.SyncID;
        //        sids.Add(new SyncID { SID = devPP.SyncID, Count = 1 });
        //    }

        //    // now we sort the List<SIDS> to get the most popular
        //    List<SyncID> sortedSids = sids.OrderBy(o => o.Count).ToList();
        //    return sortedSids[0];
        //}

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
            var ctr = 1;
            var currDate = DateTime.Now;
            foreach (var project in projects)
            {
                if (!project.DatabaseProject)
                {
                    var projectSync = GetNewProjectSyncObject(project, currDate);
                    hlpr.InsertProjectSyncObject(projectSync);
                }
            }
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
                    return GetGitURLFromConfigFile(fileName);
                // we are not in the directory where config file is
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

            var projectSync = new ProjectSync
            {
                ID = syncID,
                DevProjectName = project.DevProjectName,
                CreatedDate = currDate,
                GitURL = project.GitURL,
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
    }

    #endregion
}
