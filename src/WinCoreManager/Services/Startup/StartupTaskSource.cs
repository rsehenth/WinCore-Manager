using WinCoreManager.Localization;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace WinCoreManager.Services.Startup;

internal sealed class StartupTaskSource(IStartupProductReader products, StartupPolicy policy) : IStartupSource
{
    public string Label => LocalizationService.Source("Message.TaskScheduler");

    public void Read(List<StartupEntry> entries, List<string> warnings, CancellationToken token)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            var folders = new Queue<string>();
            folders.Enqueue(@"\");
            while (folders.TryDequeue(out var path))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    dynamic folder = service.GetFolder(path);
                    try
                    {
                        dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN
                        try
                        {
                            for (int i = 1, count = tasks.Count; i <= count; i++)
                            {
                                token.ThrowIfCancellationRequested();
                                try
                                {
                                    dynamic task = tasks.Item(i);
                                    try
                                    {
                                        var entry = Parse((string)task.Path, (string)task.Name, (bool)task.Enabled,
                                            (string)task.Xml);
                                        if (entry is not null) entries.Add(entry);
                                    }
                                    finally { Marshal.ReleaseComObject(task); }
                                }
                                catch (Exception ex) when (StartupErrors.Expected(ex)) { Warn(); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(tasks); }

                        dynamic children = folder.GetFolders(0);
                        try
                        {
                            for (int i = 1, count = children.Count; i <= count; i++)
                            {
                                token.ThrowIfCancellationRequested();
                                try
                                {
                                    dynamic child = children.Item(i);
                                    try
                                    {
                                        string childPath = child.Path;
                                        if (!childPath.Equals(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase))
                                            folders.Enqueue(childPath);
                                    }
                                    finally { Marshal.ReleaseComObject(child); }
                                }
                                catch (Exception ex) when (StartupErrors.Expected(ex)) { Warn(); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(children); }
                    }
                    finally { Marshal.ReleaseComObject(folder); }
                }
                catch (Exception ex) when (StartupErrors.Expected(ex)) { Warn(); }
            }
        }
        finally { Marshal.ReleaseComObject(service); }

        void Warn() => warnings.Add(LocalizationService.Source("Message.TaskSchedulerCouldNotReadSomeTasksOr"));
    }

    internal StartupEntry? Parse(string path, string name, bool enabled, string xml)
    {
        using var reader = XmlReader.Create(new System.IO.StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576,
        });
        var root = XDocument.Load(reader).Root;
        if (root is null) return null;
        var ns = root.Name.Namespace;
        var triggers = root.Element(ns + "Triggers")?.Elements()
            .Where(e => e.Name == ns + "LogonTrigger" || e.Name == ns + "BootTrigger").ToArray();
        if (triggers is null || triggers.Length == 0) return null;
        var actions = root.Element(ns + "Actions")?.Elements().ToArray();
        var target = actions is [var action] && action.Name == ns + "Exec"
            ? StartupPaths.Executable((string?)action.Element(ns + "Command") ?? "",
                (string?)action.Element(ns + "Arguments"), expand: true)
            : StartupTarget.Unresolved(LocalizationService.Source("Message.TheTaskHasAnIndirectActionOrMultiple"));
        var product = products.Read(target.Path);
        var active = enabled && triggers.Any(t => (string?)t.Element(ns + "Enabled") is not ("false" or "0"));
        return new($"task:{path}", name, product?.Publisher, StartupSource.ScheduledTask,
            active ? StartupStatus.Enabled : StartupStatus.Disabled, path, target.Path, target.Arguments,
            target.Problem, policy.Recognized(name, product), LocalizationService.Source("Message.ScheduledTasksAreReadOnly"));
    }
}
