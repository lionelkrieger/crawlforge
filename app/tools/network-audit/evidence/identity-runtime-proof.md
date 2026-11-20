# Criterion #2 — runtime identity-store proof (assembly-load monitoring)

Lifecycle covered: first launch → idle → reporting → diagnostics listing.

- Assemblies observed loaded: 208
- WMI/directory/automation assemblies loaded: **0** (required: 0)
- Registry-assembly loads attributed to product code: **0** (required: 0)
- Product assemblies loaded at monitor start (module init): 3
  - CrawlForge.Contracts, CrawlForge.Crawler, CrawlForge.Host (test-platform discovery, before any product execution)
- Non-loopback connections during lifecycle: 0

Reasoning: OS identity stores (WMI, directory services) are reachable from
managed code only through the hard-forbidden SDK assemblies — none loaded.
Microsoft.Win32.Registry may be loaded by the framework itself (observed:
System.Net.Quic TLS 1.3 protocol-configuration check during Kestrel startup).
Attribution uses the immediate caller of each load: framework frames are
framework reads; a product immediate caller would be a defect. The static
NR-01 layer proves product sources contain zero registry/WMI/identity API
usage or native imports.

## Load stack: Microsoft.Win32.Registry
```
   at System.Environment.get_StackTrace()
   at CrawlForge.Boundary.Tests.Harness.AssemblyLoadMonitor.<.ctor>b__4_3(Object _, AssemblyLoadEventArgs args) in E:\Crawlforge\app\tests\CrawlForge.Boundary.Tests\Harness\AssemblyLoadMonitor.cs:line 62
   at System.Net.Quic.MsQuicApi.IsTls13Disabled(Boolean isServer)
   at System.Net.Quic.MsQuicApi..cctor()
   at System.Net.Quic.QuicListener.get_IsSupported()
   at Microsoft.AspNetCore.Hosting.WebHostBuilderQuicExtensions.UseQuic(IWebHostBuilder hostBuilder)
   at Microsoft.AspNetCore.Hosting.WebHostBuilderQuicExtensions.UseQuic(IWebHostBuilder hostBuilder, Action`1 configureOptions)
   at Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.UseKestrel(IWebHostBuilder hostBuilder, Action`2 configureOptions)
   at Microsoft.AspNetCore.WebHost.ConfigureWebDefaults(IWebHostBuilder builder)
   at Microsoft.Extensions.Hosting.GenericHostBuilderExtensions.<>c__DisplayClass1_0.<ConfigureWebHostDefaults>b__0(IWebHostBuilder webHostBuilder)
   at Microsoft.Extensions.Hosting.GenericHostWebHostBuilderExtensions.ConfigureWebHost(IHostBuilder builder, Func`3 createWebHostBuilder, Action`1 configure, Action`1 configureWebHostBuilder)
   at Microsoft.AspNetCore.Builder.WebApplicationBuilder..ctor(WebApplicationOptions options, Action`1 configureDefaults)
   at Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(String[] args)
   at CrawlForge.Host.CrawlForgeBackend.StartAsync(String[] args, Action`1 configureBuilder, CancellationToken cancellationToken) in E:\Crawlforge\app\src\backend\CrawlForge.Host\CrawlForgeBackend.cs:line 80
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at CrawlForge.Host.CrawlForgeBackend.StartAsync(String[] args, Action`1 configureBuilder, CancellationToken cancellationToken)
   at CrawlForge.Boundary.Tests.Harness.BackendFixture.StartAsync() in E:\Crawlforge\app\tests\CrawlForge.Boundary.Tests\Harness\BackendFixture.cs:line 39
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at CrawlForge.Boundary.Tests.Harness.BackendFixture.StartAsync()
   at CrawlForge.Boundary.Tests.AirGap.IdleZeroOutboundTests.IdleAndReporting_WithNoDestinations_ProduceZeroOutbound() in E:\Crawlforge\app\tests\CrawlForge.Boundary.Tests\AirGap\IdleZeroOutboundTests.cs:line 20
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at CrawlForge.Boundary.Tests.AirGap.IdleZeroOutboundTests.IdleAndReporting_WithNoDestinations_ProduceZeroOutbound()
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
   at System.Reflection.RuntimeMethodInfo.Invoke(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)
   at System.Reflection.MethodBase.Invoke(Object obj, Object[] parameters)
   at Xunit.Sdk.TestInvoker`1.CallTestMethod(Object testClassInstance) in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestInvoker.cs:line 149
   at Xunit.Sdk.TestInvoker`1.<>c__DisplayClass47_0.<<InvokeTestMethodAsync>b__1>d.MoveNext() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestInvoker.cs:line 256
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestInvoker`1.<>c__DisplayClass47_0.<InvokeTestMethodAsync>b__1()
   at Xunit.Sdk.ExecutionTimer.AggregateAsync(Func`1 asyncAction) in /_/src/xunit.execution/Sdk/Frameworks/ExecutionTimer.cs:line 48
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.ExecutionTimer.AggregateAsync(Func`1 asyncAction)
   at Xunit.Sdk.TestInvoker`1.<>c__DisplayClass47_0.<InvokeTestMethodAsync>b__0() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestInvoker.cs:line 215
   at Xunit.Sdk.ExceptionAggregator.RunAsync(Func`1 code) in /_/src/xunit.core/Sdk/ExceptionAggregator.cs:line 90
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.ExceptionAggregator.RunAsync(Func`1 code)
   at Xunit.Sdk.TestInvoker`1.InvokeTestMethodAsync(Object testClassInstance) in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestInvoker.cs:line 214
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestInvoker`1.InvokeTestMethodAsync(Object testClassInstance)
   at Xunit.Sdk.XunitTestInvoker.InvokeTestMethodAsync(Object testClassInstance) in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestInvoker.cs:line 112
   at Xunit.Sdk.TestInvoker`1.<RunAsync>b__46_0() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestInvoker.cs:line 180
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestInvoker`1.<RunAsync>b__46_0()
   at Xunit.Sdk.ExceptionAggregator.RunAsync[T](Func`1 code) in /_/src/xunit.core/Sdk/ExceptionAggregator.cs:line 107
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.ExceptionAggregator.RunAsync[T](Func`1 code)
   at Xunit.Sdk.TestInvoker`1.RunAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestInvoker.cs:line 163
   at Xunit.Sdk.XunitTestRunner.InvokeTestMethodAsync(ExceptionAggregator aggregator) in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestRunner.cs:line 88
   at Xunit.Sdk.XunitTestRunner.InvokeTestAsync(ExceptionAggregator aggregator) in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestRunner.cs:line 70
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.XunitTestRunner.InvokeTestAsync(ExceptionAggregator aggregator)
   at Xunit.Sdk.TestRunner`1.<>c__DisplayClass43_0.<RunAsync>b__0() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestRunner.cs:line 149
   at Xunit.Sdk.ExceptionAggregator.RunAsync[T](Func`1 code) in /_/src/xunit.core/Sdk/ExceptionAggregator.cs:line 107
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.ExceptionAggregator.RunAsync[T](Func`1 code)
   at Xunit.Sdk.TestRunner`1.RunAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestRunner.cs:line 149
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestRunner`1.RunAsync()
   at Xunit.Sdk.XunitTestCaseRunner.RunTestAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestCaseRunner.cs:line 140
   at Xunit.Sdk.TestCaseRunner`1.RunAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestCaseRunner.cs:line 82
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestCaseRunner`1.RunAsync()
   at Xunit.Sdk.XunitTestCase.RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, Object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) in /_/src/xunit.execution/Sdk/Frameworks/XunitTestCase.cs:line 170
   at Xunit.Sdk.XunitTestMethodRunner.RunTestCaseAsync(IXunitTestCase testCase) in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestMethodRunner.cs:line 45
   at Xunit.Sdk.TestMethodRunner`1.RunTestCasesAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestMethodRunner.cs:line 136
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestMethodRunner`1.RunTestCasesAsync()
   at Xunit.Sdk.TestMethodRunner`1.RunAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestMethodRunner.cs:line 106
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestMethodRunner`1.RunAsync()
   at Xunit.Sdk.XunitTestClassRunner.RunTestMethodAsync(ITestMethod testMethod, IReflectionMethodInfo method, IEnumerable`1 testCases, Object[] constructorArguments) in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestClassRunner.cs:line 206
   at Xunit.Sdk.TestClassRunner`1.RunTestMethodsAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestClassRunner.cs:line 227
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestClassRunner`1.RunTestMethodsAsync()
   at Xunit.Sdk.TestClassRunner`1.RunAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestClassRunner.cs:line 175
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestClassRunner`1.RunAsync()
   at Xunit.Sdk.XunitTestCollectionRunner.RunTestClassAsync(ITestClass testClass, IReflectionTypeInfo class, IEnumerable`1 testCases) in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestCollectionRunner.cs:line 185
   at Xunit.Sdk.TestCollectionRunner`1.RunTestClassesAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestCollectionRunner.cs:line 130
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestCollectionRunner`1.RunTestClassesAsync()
   at Xunit.Sdk.TestCollectionRunner`1.RunAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestCollectionRunner.cs:line 101
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestCollectionRunner`1.RunAsync()
   at Xunit.Sdk.XunitTestAssemblyRunner.RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable`1 testCases, CancellationTokenSource cancellationTokenSource) in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestAssemblyRunner.cs:line 346
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.XunitTestAssemblyRunner.RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable`1 testCases, CancellationTokenSource cancellationTokenSource)
   at Xunit.Sdk.TestAssemblyRunner`1.RunTestCollectionsAsync(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource) in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestAssemblyRunner.cs:line 245
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestAssemblyRunner`1.RunTestCollectionsAsync(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource)
   at Xunit.Sdk.XunitTestAssemblyRunner.<>n__0(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource)
   at Xunit.Sdk.XunitTestAssemblyRunner.RunTestCollectionsAsync(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource) in /_/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestAssemblyRunner.cs:line 228
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.XunitTestAssemblyRunner.RunTestCollectionsAsync(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource)
   at Xunit.Sdk.TestAssemblyRunner`1.RunAsync() in /_/src/xunit.execution/Sdk/Frameworks/Runners/TestAssemblyRunner.cs:line 210
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.TestAssemblyRunner`1.RunAsync()
   at Xunit.Sdk.XunitTestFrameworkExecutor.RunTestCases(IEnumerable`1 testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions) in /_/src/xunit.execution/Sdk/Frameworks/XunitTestFrameworkExecutor.cs:line 96
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Sdk.XunitTestFrameworkExecutor.RunTestCases(IEnumerable`1 testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
   at Xunit.Sdk.TestFrameworkExecutor`1.RunTests(IEnumerable`1 testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions) in /_/src/xunit.execution/Sdk/Frameworks/TestFrameworkExecutor.cs:line 100
   at Xunit.Runner.v2.Xunit2.Run(IMessageSink messageSink, FrontControllerRunSettings settings)
   at Xunit.XunitFrontController.Run(IMessageSink messageSink, FrontControllerRunSettings settings)
   at Xunit.Runner.VisualStudio.VsTestRunner.RunTestsInAssembly(IRunContext runContext, IFrameworkHandle frameworkHandle, LoggerHelper logger, TestPlatformContext testPlatformContext, RunSettings runSettings, IMessageSink reporterMessageHandler, AssemblyRunInfo runInfo)
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Runner.VisualStudio.VsTestRunner.RunTestsInAssembly(IRunContext runContext, IFrameworkHandle frameworkHandle, LoggerHelper logger, TestPlatformContext testPlatformContext, RunSettings runSettings, IMessageSink reporterMessageHandler, AssemblyRunInfo runInfo)
   at Xunit.Runner.VisualStudio.VsTestRunner.<>c__DisplayClass34_0.<<RunTestsInAssemblyAsync>b__0>d.MoveNext()
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at Xunit.Runner.VisualStudio.VsTestRunner.<>c__DisplayClass34_0.<RunTestsInAssemblyAsync>b__0(Object _)
   at System.Threading.ThreadPoolWorkQueue.Dispatch()
   at System.Threading.PortableThreadPool.WorkerThread.WorkerThreadStart()
   at System.Threading.Thread.StartCallback()
```

## Loaded assemblies
- AngleSharp
- AngleSharp.XPath
- Anonymously Hosted DynamicMethods Assembly
- CrawlForge.Api
- CrawlForge.Audit
- CrawlForge.Boundary.Tests
- CrawlForge.Contracts
- CrawlForge.Crawler
- CrawlForge.Data.ContentStore
- CrawlForge.Data.Migrations
- CrawlForge.Data.MySql
- CrawlForge.Data.Repositories
- CrawlForge.Data.Sqlite
- CrawlForge.Export
- CrawlForge.GovernanceAnalyzer
- CrawlForge.Host
- CrawlForge.Intelligence
- CrawlForge.Mcp
- CrawlForge.Security
- FluentFTP
- Microsoft.AspNetCore
- Microsoft.AspNetCore.Authentication
- Microsoft.AspNetCore.Authentication.Abstractions
- Microsoft.AspNetCore.Authentication.Core
- Microsoft.AspNetCore.Authorization
- Microsoft.AspNetCore.Authorization.Policy
- Microsoft.AspNetCore.Connections.Abstractions
- Microsoft.AspNetCore.Cors
- Microsoft.AspNetCore.HostFiltering
- Microsoft.AspNetCore.Hosting
- Microsoft.AspNetCore.Hosting.Abstractions
- Microsoft.AspNetCore.Hosting.Server.Abstractions
- Microsoft.AspNetCore.Http
- Microsoft.AspNetCore.Http.Abstractions
- Microsoft.AspNetCore.Http.Connections
- Microsoft.AspNetCore.Http.Connections.Common
- Microsoft.AspNetCore.Http.Extensions
- Microsoft.AspNetCore.Http.Features
- Microsoft.AspNetCore.Http.Results
- Microsoft.AspNetCore.HttpOverrides
- Microsoft.AspNetCore.Metadata
- Microsoft.AspNetCore.Mvc
- Microsoft.AspNetCore.Mvc.Abstractions
- Microsoft.AspNetCore.Mvc.ApiExplorer
- Microsoft.AspNetCore.Mvc.Core
- Microsoft.AspNetCore.Mvc.Cors
- Microsoft.AspNetCore.Mvc.DataAnnotations
- Microsoft.AspNetCore.Routing
- Microsoft.AspNetCore.Routing.Abstractions
- Microsoft.AspNetCore.Server.HttpSys
- Microsoft.AspNetCore.Server.IIS
- Microsoft.AspNetCore.Server.IISIntegration
- Microsoft.AspNetCore.Server.Kestrel
- Microsoft.AspNetCore.Server.Kestrel.Core
- Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes
- Microsoft.AspNetCore.Server.Kestrel.Transport.Quic
- Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets
- Microsoft.AspNetCore.SignalR
- Microsoft.AspNetCore.SignalR.Common
- Microsoft.AspNetCore.SignalR.Core
- Microsoft.AspNetCore.SignalR.Protocols.Json
- Microsoft.AspNetCore.WebSockets
- Microsoft.AspNetCore.WebUtilities
- Microsoft.CodeAnalysis
- Microsoft.CodeAnalysis.CSharp
- Microsoft.Data.Sqlite
- Microsoft.Extensions.Configuration
- Microsoft.Extensions.Configuration.Abstractions
- Microsoft.Extensions.Configuration.Binder
- Microsoft.Extensions.Configuration.CommandLine
- Microsoft.Extensions.Configuration.EnvironmentVariables
- Microsoft.Extensions.Configuration.FileExtensions
- Microsoft.Extensions.Configuration.Json
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.DependencyInjection.Abstractions
- Microsoft.Extensions.Diagnostics
- Microsoft.Extensions.Diagnostics.Abstractions
- Microsoft.Extensions.Features
- Microsoft.Extensions.FileProviders.Abstractions
- Microsoft.Extensions.FileProviders.Physical
- Microsoft.Extensions.FileSystemGlobbing
- Microsoft.Extensions.Hosting
- Microsoft.Extensions.Hosting.Abstractions
- Microsoft.Extensions.Localization.Abstractions
- Microsoft.Extensions.Logging
- Microsoft.Extensions.Logging.Abstractions
- Microsoft.Extensions.Logging.Configuration
- Microsoft.Extensions.Logging.Console
- Microsoft.Extensions.Logging.Debug
- Microsoft.Extensions.Logging.EventLog
- Microsoft.Extensions.Logging.EventSource
- Microsoft.Extensions.ObjectPool
- Microsoft.Extensions.Options
- Microsoft.Extensions.Options.ConfigurationExtensions
- Microsoft.Extensions.Primitives
- Microsoft.Extensions.Validation
- Microsoft.Net.Http.Headers
- Microsoft.TestPlatform.CommunicationUtilities
- Microsoft.TestPlatform.CoreUtilities
- Microsoft.TestPlatform.CrossPlatEngine
- Microsoft.TestPlatform.PlatformAbstractions
- Microsoft.VisualStudio.TestPlatform.Common
- Microsoft.VisualStudio.TestPlatform.ObjectModel
- Microsoft.Win32.Primitives
- Microsoft.Win32.Registry
- MySqlConnector
- PdfSharp
- PuppeteerSharp
- ReactiveExtensionsSharp
- Renci.SshNet
- SQLitePCLRaw.batteries_v2
- SQLitePCLRaw.core
- SQLitePCLRaw.provider.e_sqlite3
- System.Collections
- System.Collections.Concurrent
- System.Collections.Immutable
- System.Collections.NonGeneric
- System.Collections.Specialized
- System.ComponentModel
- System.ComponentModel.Annotations
- System.ComponentModel.EventBasedAsync
- System.ComponentModel.Primitives
- System.ComponentModel.TypeConverter
- System.Console
- System.Data.Common
- System.Diagnostics.Debug
- System.Diagnostics.DiagnosticSource
- System.Diagnostics.EventLog
- System.Diagnostics.Process
- System.Diagnostics.StackTrace
- System.Diagnostics.TextWriterTraceListener
- System.Diagnostics.TraceSource
- System.Diagnostics.Tracing
- System.Drawing.Primitives
- System.Formats.Asn1
- System.Globalization
- System.IO
- System.IO.Compression
- System.IO.Compression.Brotli
- System.IO.FileSystem.Watcher
- System.IO.MemoryMappedFiles
- System.IO.Pipelines
- System.IO.Pipes
- System.Linq
- System.Linq.Expressions
- System.Memory
- System.Net.Http
- System.Net.HttpListener
- System.Net.NameResolution
- System.Net.NetworkInformation
- System.Net.Primitives
- System.Net.Quic
- System.Net.Requests
- System.Net.Security
- System.Net.ServerSentEvents
- System.Net.Sockets
- System.Net.WebHeaderCollection
- System.Net.WebProxy
- System.Net.WebSockets
- System.Numerics.Vectors
- System.ObjectModel
- System.Private.CoreLib
- System.Private.Uri
- System.Private.Xml
- System.Private.Xml.Linq
- System.Reflection
- System.Reflection.Emit.ILGeneration
- System.Reflection.Emit.Lightweight
- System.Reflection.Extensions
- System.Reflection.Metadata
- System.Reflection.Primitives
- System.Runtime
- System.Runtime.Extensions
- System.Runtime.InteropServices
- System.Runtime.Intrinsics
- System.Runtime.Loader
- System.Runtime.Numerics
- System.Runtime.Serialization.Primitives
- System.Security.AccessControl
- System.Security.Claims
- System.Security.Cryptography
- System.Security.Cryptography.Primitives
- System.Security.Principal.Windows
- System.Text.Encoding
- System.Text.Encoding.CodePages
- System.Text.Encoding.Extensions
- System.Text.Encodings.Web
- System.Text.Json
- System.Text.RegularExpressions
- System.Threading
- System.Threading.Channels
- System.Threading.Overlapped
- System.Threading.Tasks
- System.Threading.Tasks.Extensions
- System.Threading.Thread
- System.Threading.ThreadPool
- System.Transactions.Local
- System.Xml.ReaderWriter
- System.Xml.XDocument
- WeCantSpell.Hunspell
- WebDriverBiDi
- netstandard
- testhost
- xunit.abstractions
- xunit.assert
- xunit.core
- xunit.execution.dotnet
- xunit.runner.visualstudio.testadapter
