using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OpenCodeTrafficLight
{
    internal sealed class SessionInfo
    {
        public string Id;
        public string Title;
        public string ParentId;
        public double Updated;
        public double? Idle;
        public double? Viewed;
        public string Outcome;

        public static SessionInfo From(object json)
        {
            if (json == null) return null;
            var id = Json.GetString(json, "id");
            if (string.IsNullOrEmpty(id)) return null;

            var time = Json.AsObject(Json.Get(json, "time")) ?? new Dictionary<string, object>();
            var updated = Json.GetNumber(time, "updated");
            if (updated == null) return null;

            return new SessionInfo
            {
                Id = id,
                Title = Json.GetString(json, "title") ?? "(未命名会话)",
                ParentId = Json.GetString(json, "parentID"),
                Updated = updated.Value,
                Idle = Json.GetNumber(time, "idle"),
                Viewed = Json.GetNumber(time, "viewed"),
                Outcome = Json.GetString(json, "outcome"),
            };
        }
    }

    /// <summary>
    /// 状态判定：与 macOS 版 Monitor.snapshot() 逐条对应。
    /// 优先级：权限请求 > 等你确认 > 运行中 > 失败(10min) > 完成未查看(90s) > 空闲。
    /// </summary>
    internal static class Monitor
    {
        private sealed class SessionProbe
        {
            public List<Tuple<string, string>> Permissions = new List<Tuple<string, string>>();
            public List<Tuple<string, string>> Forms = new List<Tuple<string, string>>();
        }

        public static LightState Snapshot()
        {
            var config = ServiceConfig.Load();
            if (config == null)
            {
                return new LightState(LightMode.Red, BlinkKind.None,
                    "未找到 OpenCode 服务",
                    "请先启动 OpenCode。\n已查找 ~/.local/state/opencode/service.json");
            }

            var activeRoot = Json.AsObject(Api.Get(config, "/api/session/active"));
            var activeData = Json.AsObject(Json.Get(activeRoot, "data"));
            if (activeData == null)
            {
                return new LightState(LightMode.Red, BlinkKind.None,
                    "无法读取 OpenCode 状态",
                    "OpenCode 服务可能已退出或端口发生变化。");
            }

            var listRoot = Json.AsObject(Api.Get(config, "/api/session?limit=25&order=desc"));
            var listArray = Json.AsArray(Json.Get(listRoot, "data"));
            if (listArray == null)
            {
                return new LightState(LightMode.Red, BlinkKind.None,
                    "无法读取 OpenCode 状态",
                    "会话列表读取失败。");
            }

            var runningIds = new HashSet<string>(activeData.Keys);
            var sessions = listArray.Select(SessionInfo.From).Where(s => s != null).ToList();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 只在「最可能产生待办」的会话里查权限/表单：先运行中的，再最近会话，最多 8 个
            var candidates = new List<string>(runningIds);
            foreach (var session in sessions)
            {
                if (runningIds.Contains(session.Id)) continue;
                if (candidates.Count >= 8) break;
                candidates.Add(session.Id);
            }

            var tasks = new List<Task<SessionProbe>>();
            foreach (var sid in candidates.Take(8))
            {
                var sessionId = sid;
                var title = sessions.FirstOrDefault(s => s.Id == sessionId)?.Title ?? "";
                tasks.Add(Task.Run(() => ProbeSession(config, sessionId, title)));
            }

            if (tasks.Count > 0)
            {
                try { Task.WaitAll(tasks.Cast<Task>().ToArray(), 2800); }
                catch (Exception) { /* 单个任务异常不影响整体判定 */ }
            }

            var permissions = new List<Tuple<string, string>>();
            var forms = new List<Tuple<string, string>>();
            foreach (var task in tasks)
            {
                if (task.Status != TaskStatus.RanToCompletion) continue;
                permissions.AddRange(task.Result.Permissions);
                forms.AddRange(task.Result.Forms);
            }

            // 1) 权限请求 → 红常亮
            if (permissions.Count > 0)
            {
                string detail;
                if (permissions.Count == 1)
                    detail = "权限请求：" + permissions[0].Item1 + "\n会话：" + permissions[0].Item2;
                else
                    detail = permissions.Count + " 个权限请求待处理";

                return new LightState(LightMode.Red, BlinkKind.None, "需要你处理", detail)
                {
                    Connected = true,
                    Running = runningIds.Count,
                    Perms = permissions.Count,
                    Forms = forms.Count,
                };
            }

            // 2) 等你确认 → 黄呼吸
            if (forms.Count > 0)
            {
                var form = forms[0];
                return new LightState(LightMode.Yellow, BlinkKind.YellowPulse, "等待你确认",
                    "确认请求：" + form.Item1 + "\n会话：" + form.Item2)
                {
                    Connected = true,
                    Running = runningIds.Count,
                    Forms = forms.Count,
                };
            }

            // 3) 有会话在跑 → 黄常亮
            if (runningIds.Count > 0)
            {
                var titles = sessions.Where(s => runningIds.Contains(s.Id))
                                     .Take(6)
                                     .Select(s => "• " + s.Title)
                                     .ToList();
                var detail = runningIds.Count + " 个会话正在运行";
                if (titles.Count > 0) detail += "\n" + string.Join("\n", titles);

                return new LightState(LightMode.Yellow, BlinkKind.None, "任务进行中", detail)
                {
                    Connected = true,
                    Running = runningIds.Count,
                };
            }

            var latest = sessions.FirstOrDefault(s => s.ParentId == null) ?? sessions.FirstOrDefault();

            // 4) 最近会话失败（10 分钟内）→ 红快闪
            if (latest != null && latest.Outcome == "failed" &&
                now - latest.Updated < Metrics.FailureWindowSeconds * 1000)
            {
                return new LightState(LightMode.Red, BlinkKind.RedFast, "任务失败",
                    "最近一次会话以失败结束。\n会话：" + latest.Title)
                {
                    Connected = true,
                };
            }

            // 5) 完成未查看（90 秒内）→ 绿柔闪
            if (latest != null && latest.Outcome == "succeeded" && latest.Idle != null &&
                now - latest.Idle.Value < Metrics.DoneWindowSeconds * 1000)
            {
                var viewed = latest.Viewed ?? 0;
                if (viewed < latest.Idle.Value)
                {
                    return new LightState(LightMode.Green, BlinkKind.GreenSoft, "任务完成",
                        "最近一次会话已完成，等你查看。\n会话：" + latest.Title)
                    {
                        Connected = true,
                    };
                }
            }

            // 6) 空闲 → 绿常亮
            if (latest != null)
            {
                return new LightState(LightMode.Green, BlinkKind.None, "全部完成",
                    "没有正在运行的任务。\n最近会话：" + latest.Title)
                {
                    Connected = true,
                };
            }

            return new LightState(LightMode.Green, BlinkKind.None, "全部完成", "当前没有会话。")
            {
                Connected = true,
            };
        }

        private static SessionProbe ProbeSession(ServiceConfig config, string sessionId, string title)
        {
            var probe = new SessionProbe();

            var permRoot = Json.AsObject(Api.Get(config, "/api/session/" + sessionId + "/permission"));
            var permArray = Json.AsArray(Json.Get(permRoot, "data"));
            if (permArray != null)
            {
                foreach (var item in permArray)
                {
                    var action = Json.GetString(item, "action") ?? "确认";
                    probe.Permissions.Add(Tuple.Create(action, title));
                }
            }

            var formRoot = Json.AsObject(Api.Get(config, "/api/session/" + sessionId + "/form"));
            var formArray = Json.AsArray(Json.Get(formRoot, "data"));
            if (formArray != null && formArray.Count > 0)
            {
                foreach (var item in formArray)
                {
                    var formId = Json.GetString(item, "id");
                    if (string.IsNullOrEmpty(formId)) continue;

                    // 列表不含 state，需要查详情确认仍在 pending
                    var detailRoot = Json.AsObject(
                        Api.Get(config, "/api/session/" + sessionId + "/form/" + formId));
                    var detail = Json.AsObject(Json.Get(detailRoot, "data"));
                    var state = Json.AsObject(Json.Get(detail, "state"));
                    if (state == null || Json.GetString(state, "status") != "pending") continue;

                    var formTitle = Json.GetString(item, "title") ?? "确认请求";
                    probe.Forms.Add(Tuple.Create(formTitle, title));
                }
            }

            return probe;
        }
    }
}
