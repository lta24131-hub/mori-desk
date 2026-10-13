"""Back up two indexes, then preserve only real shortcuts from a preview.

Never copies/removes source files. Does not change existing assets or memberships.
Run only with both apps closed; arguments: live.db preview.db fixture-root backup-dir.
"""
import json
import pathlib
import sqlite3
import sys

live_path, preview_path, fixture_arg, backup_arg = map(pathlib.Path, sys.argv[1:])
fixture = fixture_arg.resolve()
backup = backup_arg.resolve()
backup.mkdir(parents=True, exist_ok=False)

with sqlite3.connect(live_path) as live, sqlite3.connect(preview_path) as preview:
    for connection, name in [(live, "library-before.db"), (preview, "preview-before.db")]:
        assert connection.execute("pragma integrity_check").fetchone()[0] == "ok"
        with sqlite3.connect(backup / name) as copy:
            connection.backup(copy)
    assets = live.execute("select * from assets order by id").fetchall()
    memberships = live.execute("select * from memberships order by asset,folder").fetchall()
    existing_refs = live.execute("select * from desk_refs order by id").fetchall()
    existing_paths = {str(pathlib.Path(row[1]).resolve()).casefold() for row in existing_refs}
    added = []
    with live:
        for row in preview.execute("select id,path,name,folder,is_dir from desk_refs"):
            path = pathlib.Path(row[1]).resolve()
            if path == fixture or fixture in path.parents:
                continue
            key = str(path).casefold()
            if key in existing_paths:
                continue
            folder = row[3]
            if folder:
                category = preview.execute("select id,name from desk_ref_folders where id=?", (folder,)).fetchone()
                if category is None:
                    raise RuntimeError("Reference category missing; aborting without changes")
                for table in ["desk_ref_folders", "folders"]:
                    current = live.execute(f"select name from {table} where id=?", (folder,)).fetchone()
                    if current and current[0] != category[1]:
                        raise RuntimeError("Category collision; aborting without changes")
                live.execute("insert or ignore into desk_ref_folders(id,name) values(?,?)", category)
                live.execute("insert or ignore into folders(id,name,parent,scope) values(?,?,'','media')", category)
            live.execute("insert into desk_refs(id,path,name,folder,is_dir) values(?,?,?,?,?)", row)
            existing_paths.add(key)
            added.append({"id": row[0], "path": row[1], "exists": path.exists()})
        assert live.execute("select * from assets order by id").fetchall() == assets
        assert live.execute("select * from memberships order by asset,folder").fetchall() == memberships
        assert all(row in live.execute("select * from desk_refs").fetchall() for row in existing_refs)
        assert live.execute("pragma integrity_check").fetchone()[0] == "ok"
    print(json.dumps({"assets_unchanged": len(assets), "original_refs_preserved": len(existing_refs), "real_refs_added": added, "backup": str(backup)}, ensure_ascii=True))
