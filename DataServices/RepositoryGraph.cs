﻿using System;
using System.Collections.Generic;
using System.Linq;
using NGit;
using NGit.Api;
using NGit.Revplot;
using NGit.Revwalk;
using NGit.Treewalk;
using NGit.Treewalk.Filter;
using NGit.Util;

namespace GitScc.DataServices
{
    public class RepositoryGraph
    {
        //private string workingDirectory;
        Repository repository;

        private List<Commit> commits;
        private List<Ref> refs;
        private List<GraphNode> nodes;
        private List<GraphLink> links;
        private bool isSimplified;

        public RepositoryGraph(Repository repository)
        {
            //this.workingDirectory = repoFolder;
            this.repository = repository;
        }

        public List<Commit> Commits
        {
            get
            {
                if (commits == null)
                {
                    PlotWalk plotWalk = null;
                    try
                    {

                        plotWalk = new PlotWalk(repository);
                        var heads = repository.GetAllRefs().Values.Select(r =>
                            plotWalk.LookupCommit(repository.Resolve(r.GetObjectId().Name))).ToList();
                        plotWalk.MarkStart(heads);
                        PlotCommitList<PlotLane> pcl = new PlotCommitList<PlotLane>();
                        pcl.Source(plotWalk);
                        pcl.FillTo(200);

                        commits = pcl.Select(c => new Commit
                        {
                            Id = c.Id.Name,
                            ParentIds = c.Parents.Select(p => p.Id.Name).ToList(),
                            CommitDateRelative = RelativeDateFormatter.Format(c.GetAuthorIdent().GetWhen()),
                            CommitterName = c.GetCommitterIdent().GetName(),
                            CommitterEmail = c.GetCommitterIdent().GetEmailAddress(),
                            CommitDate = c.GetCommitterIdent().GetWhen(),
                            Message = c.GetShortMessage(),
                        }).ToList();

                        commits.ForEach(commit => commit.ChildIds =
                            commits.Where(c => c.ParentIds.Contains(commit.Id))
                                   .Select(c => c.Id).ToList());
                    }
                    finally
                    {
                        if (plotWalk != null) plotWalk.Dispose();
                    }
                    
                }
                return commits;
            }
        }

        public List<Ref> Refs
        {
            get
            {
                if (refs == null && repository != null)
                {
                    refs = (from r in repository.GetAllRefs()
                            //where !r.Value.IsSymbolic()
                            select new Ref
                            {
                                Id = r.Value.GetTarget().GetObjectId().Name,
                                RefName = r.Key,
                            }).ToList();
                }
                return refs;
            }
        }

        public List<GraphNode> Nodes
        {
            get
            {
                if (nodes == null) GenerateGraph();
                return nodes;
            }
        }

        public List<GraphLink> Links
        {
            get
            {
                if (links == null) GenerateGraph();
                return links;
            }
        }

        private void GenerateGraph()
        {
            nodes = new List<GraphNode>();
            links = new List<GraphLink>();
            var lanes = new List<string>();

            int i = 0;

            var commits = isSimplified ? SimplifiedCommits() : Commits;

            foreach (var commit in commits)
            {
                var id = commit.Id;

                var refs = from r in this.Refs
                           where r.Id == id
                           select r;
                
                var children = from c in commits
                               where c.ParentIds.Contains(id)
                               select c;

                var lane = -1;
                if (children.Count() > 1)
                {
                    lanes.Clear();
                }
                else 
                {
                    var child = children.Where(c=>c.ParentIds.IndexOf(id)==0)
                                        .Select(c=>c.Id).FirstOrDefault();

                    lane = lanes.IndexOf(child);
                }

                if (lane < 0)
                {
                    lanes.Add(id);
                    lane = lanes.Count - 1;
                }
                else
                {
                    lanes[lane] = id;
                }
                
                var node = new GraphNode 
                {
                    X = lane, Y = i++, Id = id, Message = commit.Message,
                    CommitterName = commit.CommitterName,
                    CommitDateRelative = commit.CommitDateRelative,
                    Refs = refs.ToArray(),
                };

                nodes.Add(node);
                
                foreach (var ch in children)
                {
                    var cnode = (from n in nodes
                                 where n.Id == ch.Id
                                 select n).FirstOrDefault();
                    if (cnode != null)
                    {
                        links.Add(new GraphLink
                        {
                            X1 = cnode.X,
                            Y1 = cnode.Y,
                            X2 = node.X,
                            Y2 = node.Y,
                            Id = id
                        });
                    }
                }

            }
        }

        private List<Commit> SimplifiedCommits()
        {
            foreach (var commit in Commits)
            {
                if (commit.ParentIds.Count() == 1 && commit.ChildIds.Count() == 1 && !this.Refs.Any(r=>r.Id==commit.Id))
                {
                    commit.deleted = true;
                    var cid = commit.ChildIds[0];
                    var pid = commit.ParentIds[0];

                    var parent = Commits.Where(c => c.Id == pid).First();
                    var child = Commits.Where(c => c.Id == cid).First();

                    parent.ChildIds[parent.ChildIds.IndexOf(commit.Id)] = cid;
                    child.ParentIds[child.ParentIds.IndexOf(commit.Id)] = pid;

                    commit.ChildIds.Clear();
                    commit.ParentIds.Clear();
                }
            }

            return commits.Where(c => !c.deleted).ToList();
        }

        public bool IsSimplified {
            get { return isSimplified; }
            set { isSimplified = value; commits = null; nodes = null; links = null; }
        }

        private ObjectId GetTreeIdFromCommitId(Repository repository, string commitId)
        {
            var id = repository.Resolve(commitId);
            if (id == null) return null;

            RevWalk walk = new RevWalk(repository);
            RevCommit commit = walk.ParseCommit(id);
            walk.Dispose();
            return commit == null || commit.Tree == null ? null :
                commit.Tree.Id;
        }

        internal Commit GetCommit(string commitId)
        {
            commitId = repository.Resolve(commitId).Name;
            return Commits.Where(c => c.Id.StartsWith(commitId)).FirstOrDefault();
        }

        public GitTreeObject GetTree(string commitId)
        {
            if (repository == null) return null;

            var treeId = GetTreeIdFromCommitId(repository, commitId);
            var tree = new GitTreeObject 
            { 
                Id = treeId.Name, Name = "", IsTree=true, IsExpanded= true,
                repository = this.repository 
            };

            //expand first level
            //foreach (var t in tree.Children) t.IsExpanded = true; 
            return tree;
        }

        public Change[] GetChanges(string fromCommitId, string toCommitId)
        {
            if (repository == null) return null;

            var id1 = GetTreeIdFromCommitId(repository, fromCommitId);
            var id2 = GetTreeIdFromCommitId(repository, toCommitId);
            if (id1 == null || id2 == null) return null;
            else
                return GetChanges(repository, id2, id1);
        }

        public Change[] GetChanges(string commitId)
        {
            if (repository == null) return null;
            RevWalk walk = null;
            try
            {
                var id = repository.Resolve(commitId);
                walk = new RevWalk(repository);
                RevCommit commit = walk.ParseCommit(id);
                if (commit == null || commit.ParentCount == 0) return null;

                var pid = commit.Parents[0].Id;
                var pcommit = walk.ParseCommit(pid);
                return GetChanges(repository, commit.Tree.Id, pcommit.Tree.Id);
            }
            finally
            {
                if (walk != null) walk.Dispose();
            }
        }

        #region get changes

        // Modified version of GitSharp's Commit class
        private Change[] GetChanges(Repository repository, ObjectId id1, ObjectId id2)
        {
            var list = new List<Change>();
            TreeWalk walk = new TreeWalk(repository);
            walk.Reset(id1, id2);
            walk.Recursive = true;
            walk.Filter = TreeFilter.ANY_DIFF;
            while (walk.Next())
            {
                int m0 = walk.GetRawMode(0);
                if (walk.TreeCount == 2)
                {
                    int m1 = walk.GetRawMode(1);
                    var change = new Change
                    {
                        Name = walk.PathString,
                    };
                    if (m0 != 0 && m1 == 0)
                    {
                        change.ChangeType = ChangeType.Added;
                    }
                    else if (m0 == 0 && m1 != 0)
                    {
                        change.ChangeType = ChangeType.Deleted;
                    }
                    else if (m0 != m1 && walk.IdEqual(0, 1))
                    {
                        change.ChangeType = ChangeType.TypeChanged;
                    }
                    else
                    {
                        change.ChangeType = ChangeType.Modified;
                    }
                    list.Add(change);
                }
                else
                {
                    var raw_modes = new int[walk.TreeCount - 1];
                    for (int i = 0; i < walk.TreeCount - 1; i++)
                        raw_modes[i] = walk.GetRawMode(i + 1);
                    var change = new Change
                    {
                        Name = walk.PathString,
                    };
                    if (m0 != 0 && raw_modes.All(m1 => m1 == 0))
                    {
                        change.ChangeType = ChangeType.Added;
                        list.Add(change);
                    }
                    else if (m0 == 0 && raw_modes.Any(m1 => m1 != 0))
                    {
                        change.ChangeType = ChangeType.Deleted;
                        list.Add(change);
                    }
                    else if (raw_modes.Select((m1, i) => new { Mode = m1, Index = i + 1 }).All(x => !walk.IdEqual(0, x.Index))) // TODO: not sure if this condition suffices in some special cases.
                    {
                        change.ChangeType = ChangeType.Modified;
                        list.Add(change);
                    }
                    else if (raw_modes.Select((m1, i) => new { Mode = m1, Index = i + 1 }).Any(x => m0 != x.Mode && walk.IdEqual(0, x.Index)))
                    {
                        change.ChangeType = ChangeType.TypeChanged;
                        list.Add(change);
                    }
                }
            }
            return list.ToArray();
        }
        #endregion

        internal byte[] GetFileContent(string commitId, string fileName)
        {
            if (repository == null) return null;
            RevWalk walk = null;
            try
            {
                var id = repository.Resolve(commitId);
                walk = new RevWalk(repository);
                RevCommit commit = walk.ParseCommit(id);
                if (commit == null || commit.Tree == null) return null;
                var commitTree = new Tree(repository, commit.Tree.Id, repository.Open(commit.Tree.Id).GetBytes());
                var entry = commitTree.FindBlobMember(fileName);
                if (entry != null)
                {
                    var blob = repository.Open(entry.GetId());
                    if (blob != null) return blob.GetCachedBytes();
                }
            }
            finally
            {
                if (walk != null) walk.Dispose();
            }

            return null;
        }
    }
}