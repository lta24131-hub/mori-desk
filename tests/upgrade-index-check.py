"""Snapshot a live SQLite index read-only, or compare a post-upgrade index.

Never touches source files or changes records. Runtime-added thumbnail metadata
is excluded from comparisons; managed identity, classification and user fields
must be byte-for-byte equivalent.
"""
import json
import pathlib
import sqlite3
import sys

mode, live_arg, backup_arg = sys.argv[1:]
live_path, backup_path = pathlib.Path(live_arg), pathlib.Path(backup_arg)
queries = {
    "assets": "select id,hash,name,path,ext,bytes,tags,notes,source,added,favorite,deleted,created from assets order by id",
    "folders": "select id,name,parent,scope from folders order by id",
    "memberships": "select asset,folder from memberships order by asset,folder",
    "shortcuts": "select id,path,name,folder,is_dir from desk_refs order by id",
}
with sqlite3.connect(live_path.as_uri() + "?mode=ro", uri=True) as live:
    assert live.execute("pragma integrity_check").fetchone()[0] == "ok"
    if mode == "backup":
        if backup_path.exists():
            raise RuntimeError("Refuse to replace an existing backup")
        with sqlite3.connect(backup_path) as backup:
            live.backup(backup)
    elif mode == "verify":
        with sqlite3.connect(backup_path.as_uri() + "?mode=ro", uri=True) as backup:
            # Compare all pre-existing shortcut/user display metadata, while
            # permitting additive schema migrations with their default values.
            for label, table, extra in (
                ("assets", "assets", ["hidden_all"]),
                ("shortcuts", "desk_refs", ["tags", "notes", "source", "favorite", "added", "created", "bytes", "collection", "added_recovered", "hidden_all"]),
            ):
                previous_columns = {row[1] for row in backup.execute("pragma table_info(" + table + ")")}
                fields = [column for column in extra if column in previous_columns]
                if fields:
                    queries[label + "_metadata"] = "select id," + ",".join(fields) + " from " + table + " order by id"
            if backup.execute("pragma table_info(desk_local_collections)").fetchall():
                queries["local_collections"] = "select id,name from desk_local_collections order by id"
            for label, query in queries.items():
                assert live.execute(query).fetchall() == backup.execute(query).fetchall(), label + " changed"
    else:
        raise ValueError(mode)
    print(json.dumps({"mode": mode, "integrity": "ok", "counts": {label: len(live.execute(query).fetchall()) for label, query in queries.items()}}, ensure_ascii=True))
