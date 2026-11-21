//! Tes pencarian per kolom (tab "Cari") memakai SQLite sementara — tanpa server.

use zevn_core::{DbConnection, DbEngine, DbFilter, DbSession};

fn f(column: &str, op: &str, value: &str) -> DbFilter {
    DbFilter { column: column.into(), op: op.into(), value: value.into() }
}

#[tokio::test]
async fn search_filters() {
    let dir = std::env::temp_dir().join(format!("zevn-search-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("t.sqlite");
    let conn = DbConnection {
        id: String::new(), label: String::new(), engine: DbEngine::Sqlite, host: String::new(), port: 0,
        username: String::new(), password: None, database: String::new(), file_path: path.to_string_lossy().into(),
        tls: false, ssh_host_id: None, color: "#000000".into(), updated_at: 0, deleted: false,
    };
    let s = DbSession::connect(conn, None).await.unwrap();
    s.run("CREATE TABLE siswa (id integer, nama text, kota text)".into()).await.unwrap();
    s.run("INSERT INTO siswa VALUES (1,'Adi','Medan'),(2,'Budi','Jakarta'),(3,'Citra',NULL),(4,'Dewi','Medan'),(5,'O''Neil','Bandung')".into())
        .await
        .unwrap();

    let ids = |r: zevn_core::QueryResult| r.rows.iter().map(|row| row[0].clone().unwrap()).collect::<Vec<_>>().join(",");
    let run = |filters: Vec<DbFilter>, any: bool| {
        let s = s.clone();
        async move { ids(s.search("main".into(), "siswa".into(), filters, any, 100, 0).await.unwrap()) }
    };

    assert_eq!(run(vec![f("kota", "=", "Medan")], false).await, "1,4");
    assert_eq!(run(vec![f("nama", "LIKE %...%", "di")], false).await, "1,2");
    assert_eq!(run(vec![f("kota", "=", "Medan"), f("nama", "LIKE %...%", "ew")], false).await, "4");
    assert_eq!(run(vec![f("id", "IN (...)", "2, 5")], false).await, "2,5");
    assert_eq!(run(vec![f("id", "BETWEEN", "2,3")], false).await, "2,3");
    assert_eq!(run(vec![f("id", ">=", "4")], false).await, "4,5");
    assert_eq!(run(vec![f("kota", "IS NULL", "")], false).await, "3");
    assert_eq!(run(vec![f("kota", "!=", "Medan")], false).await, "2,5");
    // Nilai kosong diabaikan → semua baris
    assert_eq!(run(vec![f("nama", "=", "")], false).await, "1,2,3,4,5");
    // Tanda kutip di nilai tidak membobol query
    assert_eq!(run(vec![f("nama", "=", "O'Neil")], false).await, "5");
    assert_eq!(run(vec![f("nama", "=", "x' OR '1'='1")], false).await, "");
    // Cari di semua kolom (OR)
    assert_eq!(run(vec![f("nama", "LIKE %...%", "medan"), f("kota", "LIKE %...%", "medan")], true).await, "1,4");

    let n = s.count_search("main".into(), "siswa".into(), vec![f("kota", "=", "Medan")], false).await.unwrap();
    assert_eq!(n, 2);
    // Paging hasil pencarian
    let p2 = s.search("main".into(), "siswa".into(), vec![f("id", ">", "1")], false, 2, 2).await.unwrap();
    assert_eq!(ids(p2), "4,5");

    let _ = std::fs::remove_dir_all(dir);
}
