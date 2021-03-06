// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.SolutionCrawler
{
    internal sealed partial class WorkCoordinatorRegistrationService
    {
        private sealed partial class WorkCoordinator
        {
            private sealed class SemanticChangeProcessor : IdleProcessor
            {
                private static readonly Func<int, DocumentId, bool, string> enqueueLogger = (t, i, b) => string.Format("[{0}] {1} - hint: {2}", t, i.ToString(), b);

                private readonly AsyncSemaphore gate;

                private readonly int correlationId;
                private readonly Workspace workspace;
                private readonly ProjectProcessor processor;

                private readonly NonReentrantLock workGate;
                private readonly Dictionary<DocumentId, Data> pendingWork;

                public SemanticChangeProcessor(
                    IAsynchronousOperationListener listener,
                    int correlationId,
                    Workspace workspace,
                    IncrementalAnalyzerProcessor documentWorkerProcessor,
                    int backOffTimeSpanInMS,
                    int projectBackOffTimeSpanInMS,
                    CancellationToken cancellationToken) :
                    base(listener, backOffTimeSpanInMS, cancellationToken)
                {
                    this.gate = new AsyncSemaphore(initialCount: 0);

                    this.correlationId = correlationId;
                    this.workspace = workspace;

                    this.processor = new ProjectProcessor(listener, correlationId, workspace, documentWorkerProcessor, projectBackOffTimeSpanInMS, cancellationToken);

                    this.workGate = new NonReentrantLock();
                    this.pendingWork = new Dictionary<DocumentId, Data>();

                    Start();
                }

                public override Task AsyncProcessorTask
                {
                    get
                    {
                        return Task.WhenAll(base.AsyncProcessorTask, this.processor.AsyncProcessorTask);
                    }
                }

                protected override Task WaitAsync(CancellationToken cancellationToken)
                {
                    return this.gate.WaitAsync(cancellationToken);
                }

                protected override async Task ExecuteAsync()
                {
                    var data = Dequeue();

                    // we have a hint. check whether we can take advantage of it
                    if (await TryEnqueueFromHint(data.Document, data.ChangedMember).ConfigureAwait(continueOnCapturedContext: false))
                    {
                        data.AsyncToken.Dispose();
                        return;
                    }

                    EnqueueFullProjectDependency(data.Document);
                    data.AsyncToken.Dispose();
                }

                private Data Dequeue()
                {
                    return DequeueWorker(this.workGate, this.pendingWork, this.CancellationToken);
                }

                private async Task<bool> TryEnqueueFromHint(Document document, SyntaxPath changedMember)
                {
                    if (changedMember == null)
                    {
                        return false;
                    }

                    // see whether we already have semantic model. otherwise, use the expansive full project dependency one
                    // TODO: if there is a reliable way to track changed member, we could use GetSemanticModel here which could
                    //       rebuild compilation from scratch
                    SemanticModel model;
                    SyntaxNode declarationNode;
                    if (!document.TryGetSemanticModel(out model) ||
                        !changedMember.TryResolve(await document.GetSyntaxRootAsync(this.CancellationToken).ConfigureAwait(false), out declarationNode))
                    {
                        return false;
                    }

                    var symbol = model.GetDeclaredSymbol(declarationNode, this.CancellationToken);
                    if (symbol == null)
                    {
                        return false;
                    }

                    return TryEnqueueFromMember(document, symbol) ||
                           TryEnqueueFromType(document, symbol);
                }

                private bool TryEnqueueFromType(Document document, ISymbol symbol)
                {
                    if (!IsType(symbol))
                    {
                        return false;
                    }

                    if (symbol.DeclaredAccessibility == Accessibility.Private)
                    {
                        EnqueueWorkItem(document, symbol);

                        Logger.Log(FunctionId.WorkCoordinator_SemanticChange_EnqueueFromType, symbol.Name);
                        return true;
                    }

                    if (IsInternal(symbol))
                    {
                        var assembly = symbol.ContainingAssembly;
                        EnqueueFullProjectDependency(document, assembly);
                        return true;
                    }

                    return false;
                }

                private bool TryEnqueueFromMember(Document document, ISymbol symbol)
                {
                    if (!IsMember(symbol))
                    {
                        return false;
                    }

                    var typeSymbol = symbol.ContainingType;

                    if (symbol.DeclaredAccessibility == Accessibility.Private)
                    {
                        EnqueueWorkItem(document, symbol);

                        Logger.Log(FunctionId.WorkCoordinator_SemanticChange_EnqueueFromMember, symbol.Name);
                        return true;
                    }

                    if (typeSymbol == null)
                    {
                        return false;
                    }

                    return TryEnqueueFromType(document, typeSymbol);
                }

                private void EnqueueWorkItem(Document document, ISymbol symbol)
                {
                    EnqueueWorkItem(document, symbol.ContainingType != null ? symbol.ContainingType.Locations : symbol.Locations);
                }

                private void EnqueueWorkItem(Document thisDocument, ImmutableArray<Location> locations)
                {
                    var solution = thisDocument.Project.Solution;
                    var projectId = thisDocument.Id.ProjectId;

                    foreach (var location in locations)
                    {
                        Contract.Requires(location.IsInSource);

                        var document = solution.GetDocument(location.SourceTree, projectId);
                        Contract.Requires(document != null);

                        if (thisDocument == document)
                        {
                            continue;
                        }

                        this.processor.EnqueueWorkItem(document);
                    }
                }

                private bool IsInternal(ISymbol symbol)
                {
                    return symbol.DeclaredAccessibility == Accessibility.Internal ||
                           symbol.DeclaredAccessibility == Accessibility.ProtectedAndInternal ||
                           symbol.DeclaredAccessibility == Accessibility.ProtectedOrInternal;
                }

                private bool IsType(ISymbol symbol)
                {
                    return symbol.Kind == SymbolKind.NamedType;
                }

                private bool IsMember(ISymbol symbol)
                {
                    return symbol.Kind == SymbolKind.Event ||
                           symbol.Kind == SymbolKind.Field ||
                           symbol.Kind == SymbolKind.Method ||
                           symbol.Kind == SymbolKind.Property;
                }

                private void EnqueueFullProjectDependency(Document document, IAssemblySymbol internalVisibleToAssembly = null)
                {
                    var self = document.Project.Id;

                    // if there is no hint (this can happen for cases such as solution/project load and etc), 
                    // we can postpond it even further
                    if (internalVisibleToAssembly == null)
                    {
                        this.processor.Enqueue(self, needDependencyTracking: true);
                        return;
                    }

                    // most likely we got here since we are called due to typing.
                    // calculate dependency here and register each affected project to the next pipe line
                    var solution = document.Project.Solution;

                    var graph = solution.GetProjectDependencyGraph();
                    foreach (var projectId in graph.GetProjectsThatTransitivelyDependOnThisProject(self).Concat(self))
                    {
                        var project = solution.GetProject(projectId);
                        if (project == null)
                        {
                            continue;
                        }

                        Compilation compilation;
                        if (project.TryGetCompilation(out compilation))
                        {
                            var assembly = compilation.Assembly;
                            if (assembly != null && !assembly.IsSameAssemblyOrHasFriendAccessTo(internalVisibleToAssembly))
                            {
                                continue;
                            }
                        }

                        this.processor.Enqueue(projectId);
                    }

                    Logger.Log(FunctionId.WorkCoordinator_SemanticChange_FullProjects, internalVisibleToAssembly == null ? "full" : "internals");
                }

                public void Enqueue(Document document, SyntaxPath changedMember)
                {
                    this.UpdateLastAccessTime();

                    using (this.workGate.DisposableWait(this.CancellationToken))
                    {
                        Data data;
                        if (this.pendingWork.TryGetValue(document.Id, out data))
                        {
                            // create new async token and dispose old one.
                            var newAsyncToken = this.Listener.BeginAsyncOperation("EnqueueSemanticChange");
                            data.AsyncToken.Dispose();

                            this.pendingWork[document.Id] = new Data(document, data.ChangedMember == changedMember ? changedMember : null, newAsyncToken);
                            return;
                        }

                        this.pendingWork.Add(document.Id, new Data(document, changedMember, this.Listener.BeginAsyncOperation("EnqueueSemanticChange")));
                        this.gate.Release();
                    }

                    Logger.Log(FunctionId.WorkCoordinator_SemanticChange_Enqueue, enqueueLogger, Environment.TickCount, document.Id, changedMember != null);
                }

                private static TValue DequeueWorker<TKey, TValue>(NonReentrantLock gate, Dictionary<TKey, TValue> map, CancellationToken cancellationToken)
                {
                    using (gate.DisposableWait(cancellationToken))
                    {
                        var first = default(KeyValuePair<TKey, TValue>);
                        foreach (var kv in map)
                        {
                            first = kv;
                            break;
                        }

                        // this is only one that removes data from the queue. so, it should always succeed
                        var result = map.Remove(first.Key);
                        Contract.Requires(result);

                        return first.Value;
                    }
                }

                private struct Data
                {
                    public readonly Document Document;
                    public readonly SyntaxPath ChangedMember;
                    public readonly IAsyncToken AsyncToken;

                    public Data(Document document, SyntaxPath changedMember, IAsyncToken asyncToken)
                    {
                        this.AsyncToken = asyncToken;
                        this.Document = document;
                        this.ChangedMember = changedMember;
                    }
                }

                private class ProjectProcessor : IdleProcessor
                {
                    private static readonly Func<int, ProjectId, string> enqueueLogger = (t, i) => string.Format("[{0}] {1}", t, i.ToString());

                    private readonly AsyncSemaphore gate;

                    private readonly int correlationId;
                    private readonly Workspace workspace;
                    private readonly IncrementalAnalyzerProcessor processor;

                    private readonly NonReentrantLock workGate;
                    private readonly Dictionary<ProjectId, Data> pendingWork;

                    public ProjectProcessor(
                        IAsynchronousOperationListener listener,
                        int correlationId,
                        Workspace workspace,
                        IncrementalAnalyzerProcessor processor,
                        int backOffTimeSpanInMS,
                        CancellationToken cancellationToken) :
                        base(listener, backOffTimeSpanInMS, cancellationToken)
                    {
                        this.correlationId = correlationId;

                        this.workspace = workspace;
                        this.processor = processor;

                        this.gate = new AsyncSemaphore(initialCount: 0);

                        this.workGate = new NonReentrantLock();
                        this.pendingWork = new Dictionary<ProjectId, Data>();

                        Start();
                    }

                    public void Enqueue(ProjectId projectId, bool needDependencyTracking = false)
                    {
                        this.UpdateLastAccessTime();

                        using (this.workGate.DisposableWait(this.CancellationToken))
                        {
                            // the project is already in the queue. nothing needs to be done
                            if (this.pendingWork.ContainsKey(projectId))
                            {
                                return;
                            }

                            var data = new Data(projectId, needDependencyTracking, this.Listener.BeginAsyncOperation("EnqueueWorkItemForSemanticChangeAsync"));
                            this.pendingWork.Add(projectId, data);
                            this.gate.Release();
                        }

                        Logger.Log(FunctionId.WorkCoordinator_Project_Enqueue, enqueueLogger, Environment.TickCount, projectId);
                    }

                    public void EnqueueWorkItem(Document document)
                    {
                        // we are shutting down
                        this.CancellationToken.ThrowIfCancellationRequested();

                        // call to this method is serialized. and only this method does the writing.
                        var priorityService = document.GetLanguageService<IWorkCoordinatorPriorityService>();
                        this.processor.Enqueue(
                            new WorkItem(document.Id, document.Project.Language, InvocationReasons.SemanticChanged,
                            priorityService != null && priorityService.IsLowPriority(document),
                            this.Listener.BeginAsyncOperation("Semantic WorkItem")));
                    }

                    protected override Task WaitAsync(CancellationToken cancellationToken)
                    {
                        return this.gate.WaitAsync(cancellationToken);
                    }

                    protected override Task ExecuteAsync()
                    {
                        var data = Dequeue();

                        var project = this.workspace.CurrentSolution.GetProject(data.ProjectId);
                        if (project == null)
                        {
                            data.AsyncToken.Dispose();
                            return Task.FromResult(true);
                        }

                        if (!data.NeedDependencyTracking)
                        {
                            EnqueueWorkItem(project);
                            data.AsyncToken.Dispose();
                            return Task.FromResult(true);
                        }

                        // do dependency tracking here with current solution
                        var solution = this.workspace.CurrentSolution;

                        var graph = solution.GetProjectDependencyGraph();
                        foreach (var projectId in graph.GetProjectsThatTransitivelyDependOnThisProject(data.ProjectId).Concat(data.ProjectId))
                        {
                            project = solution.GetProject(projectId);
                            EnqueueWorkItem(project);
                        }

                        data.AsyncToken.Dispose();
                        return Task.FromResult(true);
                    }

                    private Data Dequeue()
                    {
                        return DequeueWorker(this.workGate, this.pendingWork, this.CancellationToken);
                    }

                    private void EnqueueWorkItem(Project project)
                    {
                        if (project == null)
                        {
                            return;
                        }

                        foreach (var documentId in project.DocumentIds)
                        {
                            EnqueueWorkItem(project.GetDocument(documentId));
                        }
                    }

                    private struct Data
                    {
                        public readonly IAsyncToken AsyncToken;
                        public readonly ProjectId ProjectId;
                        public readonly bool NeedDependencyTracking;

                        public Data(ProjectId projectId, bool needDependencyTracking, IAsyncToken asyncToken)
                        {
                            this.AsyncToken = asyncToken;
                            this.ProjectId = projectId;
                            this.NeedDependencyTracking = needDependencyTracking;
                        }
                    }
                }
            }
        }
    }
}
