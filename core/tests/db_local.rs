//! Tes integrasi database. Dijalankan lewat scripts/test-db.sh (container Docker sementara).

use zevn_core::{DbConnection, DbEngine, DbFilter, DbSession, SshTarget};

fn conn(engine: DbEngine, port: u16, user: &str, pw: &str, db: &str) -> DbConnection {
    DbConnection {
        id: String::new(), label: "tes".into(), engine, host: "127.0.0.1".into(), port,
        username: user.into(), password: Some(pw.into()), database: db.into(), file_path: String::new(),
        tls: false, ssh_host_id: None, color: String::new(), updated_at: 0, deleted: false,
    }
}

async fn roundtrip(s: &DbSession, create: &str) {
    s.run(create.into()).await.expect("create");
    let ins = s.run("INSERT INTO zevn_t (id, nama) VALUES (1, 'halo'), (2, NULL)".into()).await.expect("insert");
    assert_eq!(ins.affected, Some(2));
    let r = s.run("SELECT id, nama FROM zevn_t ORDER BY id".into()).await.expect("select");
    assert_eq!(r.columns, vec!["id", "nama"]);
    assert_eq!(r.rows, vec![vec![Some("1".into()), Some("halo".into())], vec![Some("2".into()), None]]);
    let tables = s.tables().await.unwrap();
    let t = tables.iter().find(|t| t.name == "zevn_t").expect("zevn_t ada di tables()");
    let dbs = s.databases().await.unwrap();
    assert!(!dbs.is_empty());
    // Pohon ala phpMyAdmin: tabel per database, struktur, jumlah baris, halaman
    let current = if t.schema == "public" || t.schema == "main" { dbs[0].clone() } else { t.schema.clone() };
    assert!(s.tables_in(current).await.unwrap().iter().any(|t| t.name == "zevn_t"));
    let cols = s.columns(t.schema.clone(), "zevn_t".into()).await.unwrap();
    assert_eq!(cols.rows.iter().map(|r| r[0].clone().unwrap()).collect::<Vec<_>>(), vec!["id", "nama"]);
    assert_eq!(s.count_rows(t.schema.clone(), "zevn_t".into()).await.unwrap(), 2);
    let p2 = s.page(t.schema.clone(), "zevn_t".into(), 1, 1).await.unwrap();
    assert_eq!(p2.rows.len(), 1);
    // Tab "Cari": LIKE tanpa beda huruf besar/kecil, LIKE di kolom angka, IS NULL, IN, OR di semua kolom
    let f = |c: &str, op: &str, v: &str| DbFilter { column: c.into(), op: op.into(), value: v.into() };
    let ids = |r: zevn_core::QueryResult| r.rows.iter().map(|row| row[0].clone().unwrap()).collect::<Vec<_>>();
    let sch = t.schema.clone();
    let find = |fs: Vec<DbFilter>, any: bool| s.search(sch.clone(), "zevn_t".into(), fs, any, 50, 0);
    assert_eq!(ids(find(vec![f("nama", "LIKE %...%", "HAL")], false).await.unwrap()), vec!["1"]);
    assert_eq!(ids(find(vec![f("id", "LIKE %...%", "2")], false).await.unwrap()), vec!["2"]);
    assert_eq!(ids(find(vec![f("nama", "IS NULL", "")], false).await.unwrap()), vec!["2"]);
    assert_eq!(ids(find(vec![f("id", "IN (...)", "1,2"), f("nama", "=", "halo")], false).await.unwrap()), vec!["1"]);
    assert_eq!(ids(find(vec![f("id", "LIKE %...%", "alo"), f("nama", "LIKE %...%", "alo")], true).await.unwrap()), vec!["1"]);
    assert_eq!(s.count_search(sch.clone(), "zevn_t".into(), vec![f("id", ">=", "1")], false).await.unwrap(), 2);
    assert!(s.run("SELECT * FROM tabel_tidak_ada".into()).await.is_err());
}

#[tokio::test]
async fn databases() {
    let Ok(_) = std::env::var("ZEVN_DB_TEST") else { eprintln!("skip"); return };

    // PostgreSQL (TLS dicoba dulu, lalu tanpa TLS)
    let pg = DbSession::connect(conn(DbEngine::Postgres, 55432, "postgres", "rahasia", "postgres"), None).await.expect("pg connect");
    roundtrip(&pg, "CREATE TABLE zevn_t (id int, nama text)").await;
    println!("✓ PostgreSQL");

    // MySQL & MariaDB
    for (engine, port, name) in [(DbEngine::Mysql, 53306, "MySQL"), (DbEngine::Mariadb, 53307, "MariaDB")] {
        let my = DbSession::connect(conn(engine, port, "root", "rahasia", "zevn"), None).await.expect("mysql connect");
        roundtrip(&my, "CREATE TABLE zevn_t (id int, nama varchar(20))").await;
        println!("✓ {name}");
    }

    // Redis
    let rd = DbSession::connect(conn(DbEngine::Redis, 56379, "", "rahasia", "0"), None).await.expect("redis connect");
    rd.run("SET sapa \"halo dunia\"".into()).await.unwrap();
    let r = rd.run("GET sapa".into()).await.unwrap();
    assert_eq!(r.rows[0][1].as_deref(), Some("halo dunia"));
    assert!(rd.tables().await.unwrap().iter().any(|t| t.name == "sapa"));
    println!("✓ Redis");

    // SQLite
    let path = format!("{}/tes.sqlite", std::env::var("ZEVN_DB_TMP").unwrap());
    let mut lite = conn(DbEngine::Sqlite, 0, "", "", "");
    lite.file_path = path;
    let sl = DbSession::connect(lite, None).await.expect("sqlite");
    roundtrip(&sl, "CREATE TABLE zevn_t (id integer, nama text)").await;
    println!("✓ SQLite");

    // PostgreSQL lewat tunnel SSH (sshd lokal)
    let key = std::fs::read_to_string(std::env::var("ZEVN_TEST_KEY").unwrap()).unwrap();
    let ssh = SshTarget {
        hostname: "127.0.0.1".into(), port: 2224, username: std::env::var("USER").unwrap(),
        password: None, private_key: Some(key), passphrase: None, use_agent: false, proxy: None,
    };
    let tun = DbSession::connect(conn(DbEngine::Postgres, 55432, "postgres", "rahasia", "postgres"), Some(ssh)).await.expect("tunnel connect");
    let r = tun.run("SELECT count(*) AS n FROM zevn_t".into()).await.unwrap();
    assert_eq!(r.rows[0][0].as_deref(), Some("2"));
    println!("✓ PostgreSQL lewat tunnel SSH");
}
