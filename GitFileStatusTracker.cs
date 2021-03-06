﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NGit;
using NGit.Api;
using NGit.Diff;
using NGit.Dircache;
using NGit.Revwalk;
using NGit.Storage.File;
using NGit.Treewalk;
using NGit.Treewalk.Filter;
using NGit.Ignore;
using GitScc.DataServices;

namespace GitScc
{
    public class GitFileStatusTracker : IDisposable
    {
        private string initFolder;

        private Repository repository;
        private Tree commitTree;
        private GitIndex index;
        private IList<IgnoreRule> ignoreRules;

        //private IgnoreHandler ignoreHandler;

        private Dictionary<string, GitFileStatus> cache;

        public GitFileStatusTracker(string workingFolder)
        {
            this.cache = new Dictionary<string, GitFileStatus>();
            this.initFolder = workingFolder;
            Refresh();
        }

        public void Refresh()
        {
            this.cache.Clear();
            this.changedFiles = null;
            this.repositoryGraph = null;

            if (!string.IsNullOrEmpty(initFolder))
            {
                try
                {
                    this.repository = Git.Open(initFolder).GetRepository();

                    if (this.repository != null)
                    {
                        var id = repository.Resolve(Constants.HEAD);
                        //var commit = repository.MapCommit(id);
                        //this.commitTree = (commit != null ? commit.TreeEntry : new Tree(repository));
                        if (id == null)
                        {
                            this.commitTree = new Tree(repository);
                        }
                        else
                        {
                            var treeId = ObjectId.FromString(repository.Open(id).GetBytes(), 5);
                            this.commitTree = new Tree(repository, treeId, repository.Open(treeId).GetBytes());
                        }
                        this.index = repository.GetIndex();
                        this.index.RereadIfNecessary();

                        ignoreRules = File.ReadAllLines(Path.Combine(this.initFolder, Constants.GITIGNORE_FILENAME))
                                          .Where(line => !line.StartsWith("#") && line.Trim().Length > 0)
                                          .Select(line => new IgnoreRule(line)).ToList();
                    }
                }
                catch (Exception ex)
                {
                }
            }
        }

        public void Dispose()
        {
            if (this.repository != null) this.repository.Close();
        }

        public string GitWorkingDirectory
        {
            get
            {
                return this.repository == null ? null :
                    this.repository.WorkTree;
            }
        }

        public bool HasGitRepository
        {
            get { return this.repository != null; }
        }

        public GitFileStatus GetFileStatus(string fileName)
        {
            if (!HasGitRepository || string.IsNullOrEmpty(fileName))
                return GitFileStatus.NotControlled;

            if (!this.cache.ContainsKey(fileName))
            {
                var status = GetFileStatusNoCache(fileName);
                this.cache.Add(fileName, status);
                //Debug.WriteLine(string.Format("GetFileStatus {0} - {1}", fileName, status));
                return status;
            }
            else
            {
                return this.cache[fileName];
            }
        }

        private GitFileStatus GetFileStatusNoCache(string fileName)
        {
            //Debug.WriteLine(string.Format("===+ GetFileStatusNoCache {0}", fileName));

            var fileNameRel = GetRelativeFileName(fileName);

            TreeEntry treeEntry = this.commitTree.FindBlobMember(fileNameRel);
            GitIndex.Entry indexEntry = this.index.GetEntry(fileNameRel);

            //the order of 'if' below is important
            if (indexEntry != null)
            {
                if (treeEntry == null)
                {
                    return GitFileStatus.Added;
                }
                if (treeEntry != null && !treeEntry.GetId().Equals(indexEntry.GetObjectId()))
                {
                    return GitFileStatus.Staged;
                }
                if (!File.Exists(fileName))
                {
                    return GitFileStatus.Deleted;
                }
                if (File.Exists(fileName) && indexEntry.IsModified(repository.WorkTree, true))
                {
                    return GitFileStatus.Modified;
                }
                if (indexEntry.GetStage() != 0)
                {
                    return GitFileStatus.MergeConflict;
                }
                if (treeEntry != null && treeEntry.GetId().Equals(indexEntry.GetObjectId()))
                {
                    return GitFileStatus.Tracked;
                }
            }
            else // <-- index entry == null
            {
                if (treeEntry != null && !(treeEntry is Tree))
                {
                    return GitFileStatus.Removed;
                }
                if (File.Exists(fileName))
                {
                    if (ignoreRules != null && ignoreRules.Any(rule => rule.IsMatch(fileName, false)))
                    {
                        return GitFileStatus.Ignored;
                    }

                    return GitFileStatus.New;
                }
            }

            return GitFileStatus.NotControlled;
        }

        private string GetRelativeFileName(string fileName)
        {
            return GetRelativePath(repository.WorkTree, fileName);
        }

        /// <summary>
        /// Computes relative path, where path is relative to reference_path
        /// </summary>
        /// <param name="reference_path"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string GetRelativePath(string reference_path, string path)
        {
            if (reference_path == null)
                throw new ArgumentNullException("reference_path");
            if (path == null)
                throw new ArgumentNullException("path");
            //reference_path = reference_path.Replace('/', '\\');
            //path = path.Replace('/', '\\');
            bool isRooted = Path.IsPathRooted(reference_path) && Path.IsPathRooted(path);
            if (isRooted)
            {
                bool isDifferentRoot = string.Compare(Path.GetPathRoot(reference_path), Path.GetPathRoot(path), true) != 0;
                if (isDifferentRoot)
                    return path;
            }
            var relativePath = new StringCollection();
            string[] fromDirectories = Regex.Split(reference_path, @"[/\\]+");
            string[] toDirectories = Regex.Split(path, @"[/\\]+");
            int length = Math.Min(fromDirectories.Length, toDirectories.Length);
            int lastCommonRoot = -1;
            // find common root
            for (int x = 0; x < length; x++)
            {
                if (string.Compare(fromDirectories[x],
                      toDirectories[x], true) != 0)
                    break;
                lastCommonRoot = x;
            }
            if (lastCommonRoot == -1)
                return string.Join(Path.DirectorySeparatorChar.ToString(), toDirectories);
            // add relative folders in from path
            for (int x = lastCommonRoot + 1; x < fromDirectories.Length; x++)
                if (fromDirectories[x].Length > 0)
                    relativePath.Add("..");
            // add to folders to path
            for (int x = lastCommonRoot + 1; x < toDirectories.Length; x++)
                relativePath.Add(toDirectories[x]);
            // create relative path
            string[] relativeParts = new string[relativePath.Count];
            relativePath.CopyTo(relativeParts, 0);
            string newPath = string.Join(Path.DirectorySeparatorChar.ToString(), relativeParts);
            return newPath;
        }

        public byte[] GetFileContent(string fileName)
        {
            if (!HasGitRepository || string.IsNullOrEmpty(fileName))
                return null;

            fileName = GetRelativeFileName(fileName);

            try
            {
                var entry = commitTree.FindBlobMember(fileName);
                if (entry != null)
                {
                    var blob = repository.Open(entry.GetId());
                    if (blob != null) return blob.GetCachedBytes();
                }

            }
            catch { } // better than crash

            return null;
        }

        public string CurrentBranch
        {
            get
            {
                return this.HasGitRepository ? this.repository.GetBranch() : "";
            }
        }

        /// <summary>
        /// Search Git Repository in folder and its parent folders 
        /// </summary>
        /// <param name="folder">starting folder</param>
        /// <returns>folder that has .git subfolder</returns>
        public static string GetRepositoryDirectory(string folder)
        {
            if(string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

            var directory = new DirectoryInfo(folder);

            if (directory.GetDirectories(Constants.DOT_GIT).Length > 0)
            {
                return folder;
            }

            return directory.Parent == null ? null :
                   GetRepositoryDirectory(directory.Parent.FullName);
        }

        public override string ToString()
        {
            return repository == null ? "[no repo]" : this.GitWorkingDirectory;
        }

        /// <summary>
        /// Requires absolute path
        /// </summary>
        /// <param name="fileName"></param>
        public void UnStageFile(string fileName)
        {
            var fileNameRel = GetRelativeFileName(fileName);
            TreeEntry treeEntry = this.commitTree.FindBlobMember(fileNameRel);

            //fileName = Path.Combine(initFolder, fileName);

            if (!this.HasGitRepository) return;
            this.index.RereadIfNecessary();

            this.index.Remove(repository.WorkTree, fileName);

            if (treeEntry != null)
            {
                this.index.AddEntry(treeEntry);
            }

            this.index.Write();
            this.cache[fileName] = GetFileStatusNoCache(fileName);
        }

        /// <summary>
        /// Requires absolute path
        /// </summary>
        /// <param name="fileName"></param>
        public void StageFile(string fileName)
        {
            //fileName = Path.Combine(initFolder, fileName);

            if (!this.HasGitRepository) return;
            this.index.RereadIfNecessary();

            if (File.Exists(fileName))
            {
                var content = File.ReadAllBytes(fileName);
                this.index.Add(repository.WorkTree, fileName, content);
            }
            else
            {
                //stage deleted
                this.index.Remove(repository.WorkTree, fileName);
            }
            this.index.Write();
            this.cache[fileName] = GetFileStatusNoCache(fileName);
        }

        public void RemoveFile(string fileName)
        {
            if (!this.HasGitRepository) return;

            this.index.RereadIfNecessary();
            this.index.Remove(repository.WorkTree, fileName);
            this.index.Write();
            this.cache[fileName] = GetFileStatusNoCache(fileName);
        }

        /// <summary>
        /// Diff working file with last commit
        /// </summary>
        /// <param name="fileName">Expect relative path</param>
        /// <returns></returns>
        public string DiffFile(string fileName)
        {
            try
            {
                if (!this.HasGitRepository) return "";

                HistogramDiff hd = new HistogramDiff();
                hd.SetFallbackAlgorithm(null);

                var fullName = GetFullPath(fileName);

                RawText b = new RawText(File.Exists(GetFullPath(fileName)) ?
                                        File.ReadAllBytes(fullName) : new byte[0]);
                RawText a = new RawText(GetFileContent(fileName) ?? new byte[0]);

                var list = hd.Diff(RawTextComparator.DEFAULT, a, b);

                using (Stream mstream = new MemoryStream(),
                              stream = new BufferedStream(mstream))
                {
                    DiffFormatter df = new DiffFormatter(stream);
                    df.Format(list, a, b);
                    df.Flush();
                    stream.Seek(0, SeekOrigin.Begin);
                    var ret = new StreamReader(stream).ReadToEnd();

                    return ret;
                }
            }
            catch 
            {
                return "";
            }
        }

        public string Commit(string message)
        {
            if (!this.HasGitRepository) return null;

            if (string.IsNullOrEmpty(message))
                throw new ArgumentException("Commit message must not be null or empty!", "message");

            var git = new Git(this.repository);
            var rev = git.Commit().SetMessage(message).Call();

            Refresh();

            return rev.Name;
        }

        public string AmendCommit(string message)
        {
            if (!HasGitRepository) return null;

            if (string.IsNullOrEmpty(message))
                throw new ArgumentException("Commit message must not be null or empty!", "message");

            var git = new Git(this.repository);
            var rev = git.Commit().SetAmend(true).SetMessage(message).Call();

            Refresh();

            return rev.Name;
        }

        public static void Init(string folderName)
        {
            var gitFolder = Path.Combine(folderName, Constants.DOT_GIT);
            var repo = new FileRepository(gitFolder);
            repo.Create();
            var dir = Directory.CreateDirectory(gitFolder);
            dir.Attributes = FileAttributes.Directory | FileAttributes.Hidden;  
        }

        private IEnumerable<GitFile> changedFiles;
        public IEnumerable<GitFile> ChangedFiles
        {
            get
            {
                if (changedFiles == null)
                {
                    FillCache();

                    changedFiles = from f in this.cache
                                   where f.Value != GitFileStatus.Tracked &&
                                         f.Value != GitFileStatus.NotControlled &&
                                         f.Value != GitFileStatus.Ignored
                                   select new GitFile
                                   {
                                       FileName = GetRelativeFileName(f.Key),
                                       Status = f.Value,
                                       IsStaged = f.Value == GitFileStatus.Added ||
                                                  f.Value == GitFileStatus.Staged ||
                                                  f.Value == GitFileStatus.Removed
                                   };
                }
                return changedFiles;
            }
        }

        private const int INDEX = 1;
        private const int WORKDIR = 2;

        public void FillCache()
        {
            var treeWalk = new TreeWalk(this.repository);
            treeWalk.Recursive = true;
            treeWalk.Filter = TreeFilter.ANY_DIFF;

            var id = repository.Resolve(Constants.HEAD);
            if (id != null)
            {
                treeWalk.AddTree(ObjectId.FromString(repository.Open(id).GetBytes(), 5)); //any better way?
            }
            else
            {
                treeWalk.AddTree(new EmptyTreeIterator());
            }

            treeWalk.AddTree(new DirCacheIterator(this.repository.ReadDirCache()));
            treeWalk.AddTree(new FileTreeIterator(this.repository));
            var filters = new TreeFilter[] { new SkipWorkTreeFilter(INDEX), new IndexDiffFilter(INDEX, WORKDIR) };
            treeWalk.Filter = AndTreeFilter.Create(filters);

            while (treeWalk.Next())
            {
                var fileName = GetFullPath(treeWalk.PathString);

                if (Directory.Exists(fileName)) continue; // this excludes sub modules

                var status = GetFileStatusNoCache(fileName);
                this.cache[fileName] = status;
                //Debug.WriteLine(string.Format("==== Fill cache for {0} <- {1}", fileName, status));
            }
        }

        private string GetFullPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return this.GitWorkingDirectory;

            return Path.Combine(this.GitWorkingDirectory, fileName.Replace("/", "\\"));
        }

        public string LastCommitMessage
        {
            get
            {
                if (!HasGitRepository) return null;

                ObjectId headId = this.repository.Resolve(Constants.HEAD);

                if (headId != null)
                {
                    var revWalk = new RevWalk(this.repository);
                    revWalk.MarkStart(revWalk.LookupCommit(headId));
                    foreach (RevCommit c in revWalk)
                    {
                        return c.GetFullMessage();
                    }
                }
                return "";
            }
        }

        public Repository Repository
        {
            get { return repository; }
        }

        RepositoryGraph repositoryGraph;
        public RepositoryGraph RepositoryGraph
        {
            get
            {
                if (repositoryGraph == null)
                {
                    repositoryGraph = HasGitRepository ? new RepositoryGraph(this.repository) : null;
                }
                return repositoryGraph;
            }
        }
    }
}
