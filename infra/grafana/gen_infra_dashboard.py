#!/usr/bin/env python3
"""Generator for the IntuneDeviceActions Infrastructure Health Grafana dashboard.

Produces infrastructure-health-dashboard.json. Keep this script as the source of
truth: edit it and re-run `python gen_infra_dashboard.py` rather than hand-editing
the JSON. The dashboard is portable across stamps via the constant template
variables (subscription / resourceGroup / prefix / suffix) — no resource IDs are
hardcoded.
"""
import json
import os

DS_AZURE = {"type": "grafana-azure-monitor-datasource", "uid": "${DS_AZURE}"}
DS_AI = {"type": "grafana-azure-monitor-datasource", "uid": "${DS_AI}"}

SUB = "${subscription}"
RG = "${resourceGroup}"

# role-name -> legend alias for the Function Apps / Web sites
WEB_SITES = {
    "web": "web",
    "proc": "proc",
    "wipe": "wipe",
    "autopilot": "autopilot",
    "bitlocker": "bitlocker",
    "rename": "rename",
    "portal": "portal",
}

# storage infix (between prefix and suffix) -> legend alias
STORAGE = {
    "stw": "web (stw)",
    "stp": "proc (stp)",
    "stwp": "wipe (stwp)",
    "stap": "autopilot (stap)",
    "stbl": "bitlocker (stbl)",
    "strn": "rename (strn)",
}


def web_uri(role):
    return f"/subscriptions/{SUB}/resourceGroups/{RG}/providers/Microsoft.Web/sites/${{prefix}}-{role}-${{suffix}}"


def storage_uri(infix):
    return f"/subscriptions/{SUB}/resourceGroups/{RG}/providers/Microsoft.Storage/storageAccounts/${{prefix}}{infix}${{suffix}}"


def single_uri(kind):
    table = {
        "sb": f"/subscriptions/{SUB}/resourceGroups/{RG}/providers/Microsoft.ServiceBus/namespaces/${{prefix}}-sb-${{suffix}}",
        "appcfg": f"/subscriptions/{SUB}/resourceGroups/{RG}/providers/Microsoft.AppConfiguration/configurationStores/${{prefix}}-appcfg-${{suffix}}",
        "eg": f"/subscriptions/{SUB}/resourceGroups/{RG}/providers/Microsoft.EventGrid/topics/${{prefix}}-eg-audit-${{suffix}}",
        "aa": f"/subscriptions/{SUB}/resourceGroups/{RG}/providers/Microsoft.Automation/automationAccounts/${{prefix}}-aa-${{suffix}}",
    }
    return table[kind]


def metric_target(refid, resource_uri, namespace, metric, agg, alias,
                  time_grain="auto", dim_filters=None):
    am = {
        "resourceUri": resource_uri,
        "metricNamespace": namespace,
        "metricName": metric,
        "aggregation": agg,
        "timeGrain": time_grain,
        "alias": alias,
    }
    if dim_filters:
        am["dimensionFilters"] = dim_filters
    return {"refId": refid, "queryType": "Azure Monitor", "azureMonitor": am}


def arg_target(refid, query, result_format="table"):
    return {
        "refId": refid,
        "queryType": "Azure Resource Graph",
        "subscriptions": [SUB],
        "azureResourceGraph": {"query": query, "resultFormat": result_format},
    }


def la_target(refid, query):
    return {
        "refId": refid,
        "queryType": "Azure Log Analytics",
        "azureLogAnalytics": {"query": query, "resultFormat": "time_series"},
    }


REFIDS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"


def web_targets(namespace, metric, agg, time_grain="auto"):
    return [
        metric_target(REFIDS[i], web_uri(role), namespace, metric, agg, alias, time_grain)
        for i, (role, alias) in enumerate(WEB_SITES.items())
    ]


def storage_targets(metric, agg, time_grain="PT1M", dim_filters=None):
    return [
        metric_target(REFIDS[i], storage_uri(infix), "Microsoft.Storage/storageAccounts",
                      metric, agg, alias, time_grain, dim_filters)
        for i, (infix, alias) in enumerate(STORAGE.items())
    ]


panels = []
pid = [0]


def next_id():
    pid[0] += 1
    return pid[0]


def row(title, y):
    return {"type": "row", "title": title, "collapsed": False,
            "gridPos": {"h": 1, "w": 24, "x": 0, "y": y}, "id": next_id(), "panels": []}


def ts_panel(title, targets, x, y, w, h, ds=DS_AZURE, unit="short", desc=""):
    return {
        "type": "timeseries", "title": title, "description": desc,
        "datasource": ds, "targets": targets,
        "gridPos": {"h": h, "w": w, "x": x, "y": y}, "id": next_id(),
        "fieldConfig": {"defaults": {"unit": unit, "custom": {
            "drawStyle": "line", "lineWidth": 1, "fillOpacity": 10,
            "showPoints": "never"}}, "overrides": []},
        "options": {"legend": {"displayMode": "table", "placement": "bottom",
                               "calcs": ["lastNotNull", "max"]},
                    "tooltip": {"mode": "multi"}},
    }


def stat_panel(title, targets, x, y, w, h, ds=DS_AZURE, unit="short",
               thresholds=None, desc=""):
    steps = thresholds or [{"color": "green", "value": None}]
    return {
        "type": "stat", "title": title, "description": desc,
        "datasource": ds, "targets": targets,
        "gridPos": {"h": h, "w": w, "x": x, "y": y}, "id": next_id(),
        "fieldConfig": {"defaults": {"unit": unit, "thresholds": {
            "mode": "absolute", "steps": steps}}, "overrides": []},
        "options": {"colorMode": "background", "graphMode": "area",
                    "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False}},
    }


def table_panel(title, targets, x, y, w, h, ds=DS_AZURE, desc=""):
    return {
        "type": "table", "title": title, "description": desc,
        "datasource": ds, "targets": targets,
        "gridPos": {"h": h, "w": w, "x": x, "y": y}, "id": next_id(),
        "fieldConfig": {"defaults": {}, "overrides": []},
        "options": {"showHeader": True},
    }


# ── Overview row ─────────────────────────────────────────────────────────────
panels.append(row("Overview — health at a glance", 0))
panels.append(stat_panel(
    "Function Apps — HTTP 5xx (range)",
    web_targets("Microsoft.Web/sites", "Http5xx", "Total"),
    0, 1, 6, 5, unit="short",
    thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1}],
    desc="Server errors across all Function Apps. Should be 0."))
panels.append(stat_panel(
    "Storage — min availability %",
    storage_targets("Availability", "Average"),
    6, 1, 6, 5, unit="percent",
    thresholds=[{"color": "red", "value": None}, {"color": "yellow", "value": 99},
                {"color": "green", "value": 99.9}],
    desc="Lowest availability across the storage accounts."))
panels.append(stat_panel(
    "Service Bus — dead-lettered messages",
    [metric_target("A", single_uri("sb"), "Microsoft.ServiceBus/namespaces",
                   "DeadletteredMessages", "Average", "dead-letter")],
    12, 1, 6, 5, unit="short",
    thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1}],
    desc="Messages parked in any DLQ. Should be 0."))
panels.append(stat_panel(
    "Event Grid — publish failures (range)",
    [metric_target("A", single_uri("eg"), "Microsoft.EventGrid/topics",
                   "PublishFailCount", "Total", "fail")],
    18, 1, 6, 5, unit="short",
    thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1}],
    desc="Audit topic publish failures."))

# ── Function Apps row ────────────────────────────────────────────────────────
panels.append(row("Function Apps (Web / Proc / capability hosts / portal)", 6))
panels.append(ts_panel(
    "HTTP response time (avg)",
    web_targets("Microsoft.Web/sites", "HttpResponseTime", "Average"),
    0, 7, 12, 8, unit="s",
    desc="Average HTTP response time per app."))
panels.append(ts_panel(
    "HTTP 5xx",
    web_targets("Microsoft.Web/sites", "Http5xx", "Total"),
    12, 7, 12, 8, unit="short"))
panels.append(ts_panel(
    "Requests",
    web_targets("Microsoft.Web/sites", "Requests", "Total"),
    0, 15, 8, 8, unit="short"))
panels.append(ts_panel(
    "Function execution count",
    web_targets("Microsoft.Web/sites", "FunctionExecutionCount", "Total"),
    8, 15, 8, 8, unit="short"))
panels.append(ts_panel(
    "Memory working set (avg)",
    web_targets("Microsoft.Web/sites", "AverageMemoryWorkingSet", "Average"),
    16, 15, 8, 8, unit="bytes"))

# ── Storage row ──────────────────────────────────────────────────────────────
panels.append(row("Storage accounts", 23))
panels.append(ts_panel(
    "Availability %",
    storage_targets("Availability", "Average"),
    0, 24, 8, 8, unit="percent"))
panels.append(ts_panel(
    "Transactions",
    storage_targets("Transactions", "Total"),
    8, 24, 8, 8, unit="short"))
panels.append(ts_panel(
    "End-to-end latency (avg)",
    storage_targets("SuccessE2ELatency", "Average"),
    16, 24, 8, 8, unit="ms"))
panels.append(ts_panel(
    "Throttled transactions (ClientThrottlingError)",
    storage_targets("Transactions", "Total", dim_filters=[
        {"dimension": "ResponseType", "operator": "eq", "filters": ["ClientThrottlingError"]}]),
    0, 32, 12, 8, unit="short",
    desc="Non-zero values indicate the account is being throttled."))
panels.append(table_panel(
    "Public Network Access (PNA) per storage account",
    [arg_target("A",
                "Resources | where type =~ 'microsoft.storage/storageaccounts' "
                "and resourceGroup =~ '${resourceGroup}' "
                "| project name, publicNetworkAccess = tostring(properties.publicNetworkAccess), "
                "location, sku = tostring(sku.name) | order by name asc")],
    12, 32, 12, 8,
    desc="PNA should be Enabled in dev (an external job periodically disables it). "
         "Mirrors the hourly PNA check/enable schedule."))

# ── Service Bus row ──────────────────────────────────────────────────────────
panels.append(row("Service Bus namespace", 40))
panels.append(ts_panel(
    "Server & user errors / throttled requests",
    [metric_target("A", single_uri("sb"), "Microsoft.ServiceBus/namespaces",
                   "ServerErrors", "Total", "server errors"),
     metric_target("B", single_uri("sb"), "Microsoft.ServiceBus/namespaces",
                   "UserErrors", "Total", "user errors"),
     metric_target("C", single_uri("sb"), "Microsoft.ServiceBus/namespaces",
                   "ThrottledRequests", "Total", "throttled")],
    0, 41, 12, 8, unit="short"))
panels.append(ts_panel(
    "Incoming / outgoing messages",
    [metric_target("A", single_uri("sb"), "Microsoft.ServiceBus/namespaces",
                   "IncomingMessages", "Total", "incoming"),
     metric_target("B", single_uri("sb"), "Microsoft.ServiceBus/namespaces",
                   "OutgoingMessages", "Total", "outgoing")],
    12, 41, 12, 8, unit="short"))

# ── App Config / Event Grid / Automation row ─────────────────────────────────
panels.append(row("App Configuration · Event Grid · Automation", 49))
panels.append(ts_panel(
    "App Configuration — requests & throttling",
    [metric_target("A", single_uri("appcfg"), "Microsoft.AppConfiguration/configurationStores",
                   "HttpIncomingRequestCount", "Total", "requests"),
     metric_target("B", single_uri("appcfg"), "Microsoft.AppConfiguration/configurationStores",
                   "ThrottledHttpRequestCount", "Total", "throttled")],
    0, 50, 8, 8, unit="short"))
panels.append(ts_panel(
    "Event Grid (audit topic) — publish & dead-letter",
    [metric_target("A", single_uri("eg"), "Microsoft.EventGrid/topics",
                   "PublishSuccessCount", "Total", "success"),
     metric_target("B", single_uri("eg"), "Microsoft.EventGrid/topics",
                   "PublishFailCount", "Total", "fail"),
     metric_target("C", single_uri("eg"), "Microsoft.EventGrid/topics",
                   "DeadLetteredCount", "Total", "dead-letter")],
    8, 50, 8, 8, unit="short"))
panels.append(ts_panel(
    "Automation — runbook jobs by status",
    [metric_target("A", single_uri("aa"), "Microsoft.Automation/automationAccounts",
                   "TotalJob", "Total", "jobs",
                   dim_filters=[{"dimension": "Status", "operator": "eq", "filters": ["*"]}])],
    16, 50, 8, 8, unit="short",
    desc="Wipe / Autopilot / BitLocker / Rename runbook executions, split by status."))

# ── Availability (App Insights) row ──────────────────────────────────────────
panels.append(row("End-to-end availability (Application Insights)", 58))
panels.append(ts_panel(
    "Failed requests by role",
    [la_target("A",
               "requests | where timestamp > $__timeFrom() and timestamp < $__timeTo() "
               "| summarize failures = countif(success == false) by bin(timestamp, 5m), cloud_RoleName "
               "| order by timestamp asc")],
    0, 59, 12, 8, ds=DS_AI, unit="short"))
panels.append(ts_panel(
    "Exceptions by role",
    [la_target("A",
               "exceptions | where timestamp > $__timeFrom() and timestamp < $__timeTo() "
               "| summarize count() by bin(timestamp, 5m), cloud_RoleName | order by timestamp asc")],
    12, 59, 12, 8, ds=DS_AI, unit="short"))


def const_var(name, label, value, description=""):
    return {"name": name, "type": "constant", "label": label,
            "description": description, "query": value,
            "current": {"selected": False, "text": value, "value": value},
            "hide": 2}


dashboard = {
    "annotations": {"list": [{"builtIn": 1, "type": "dashboard",
                              "name": "Annotations & Alerts", "enable": True,
                              "iconColor": "rgba(0, 211, 255, 1)"}]},
    "description": "Infrastructure health for the IntuneDeviceActions stamp: "
                   "Function Apps, Storage (incl. PNA), Service Bus, App Configuration, "
                   "Event Grid, Automation runbooks and end-to-end availability.",
    "editable": True,
    "schemaVersion": 39,
    "tags": ["intunedeviceactions", "infrastructure", "health"],
    "templating": {"list": [
        const_var("subscription", "Subscription ID",
                  "b45c5b53-d8f3-4a4c-9fe5-5537818a9886",
                  "Azure subscription containing the stamp."),
        const_var("resourceGroup", "Resource group", "RG-INTUNE-DEVICEACTIONS"),
        const_var("prefix", "Name prefix", "devact",
                  "Resource name prefix used by the stamp (apps + storage)."),
        const_var("suffix", "Name suffix", "dev",
                  "Stamp suffix, e.g. dev / prod."),
    ]},
    "time": {"from": "now-6h", "to": "now"},
    "refresh": "5m",
    "title": "IntuneDeviceActions — Infrastructure Health",
    "uid": "idactions-infra-health",
    "panels": panels,
}

out = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "infrastructure-health-dashboard.json")
with open(out, "w", encoding="utf-8") as f:
    json.dump(dashboard, f, indent=2)
    f.write("\n")
print(f"wrote {out} with {len(panels)} panels")
