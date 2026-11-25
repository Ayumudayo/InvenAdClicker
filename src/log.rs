use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use anyhow::{Context, Result};
use chrono::TimeZone;

pub(crate) struct SessionLog {
    file: Mutex<std::fs::File>,
    console: bool,
    pub(crate) path: PathBuf,
}

impl SessionLog {
    pub(crate) fn new(dir: &Path, retention_days: Option<u32>, console: bool) -> Result<Self> {
        std::fs::create_dir_all(dir)
            .with_context(|| format!("로그 폴더 생성 실패 ({})", dir.display()))?;
        if let Some(days) = retention_days {
            cleanup_logs(dir, days);
        }
        let stamp = chrono::Local::now().format("%Y%m%d-%H%M%S");
        let mut suffix = String::new();
        let mut n = 0usize;
        let (mut file, path) = loop {
            let path = dir.join(format!("InvenAdClicker-{stamp}{suffix}.log"));
            match std::fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&path)
            {
                Ok(file) => break (file, path),
                Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                    n += 1;
                    suffix = format!(".{n}");
                }
                Err(e) => {
                    return Err(e)
                        .with_context(|| format!("로그 파일 생성 실패 ({})", path.display()))
                }
            }
        };
        file.write_all(b"\xef\xbb\xbf")?;
        if console {
            println!("로그 파일: {}", path.display());
        }
        Ok(SessionLog {
            file: Mutex::new(file),
            path,
            console,
        })
    }

    pub(crate) fn notice(&self, msg: &str) {
        if !self.console {
            eprintln!("{msg}");
        }
        self.log(msg);
    }

    pub(crate) fn log(&self, msg: &str) {
        if self.console {
            println!("{msg}");
        }
        self.record(msg);
    }

    pub(crate) fn record(&self, msg: &str) {
        let line = format!("[{}] {msg}", chrono::Local::now().format("%H:%M:%S"));
        if let Ok(mut f) = self.file.lock() {
            let _ = writeln!(f, "{line}");
        }
    }
}

fn cleanup_logs(dir: &Path, days: u32) -> usize {
    let cutoff = chrono::Local::now() - chrono::Duration::days(days as i64);
    let mut removed = 0;
    if let Ok(entries) = std::fs::read_dir(dir) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            let rest = name
                .strip_prefix("InvenAdClicker-")
                .or_else(|| name.strip_prefix("inven-http-"));
            if let Some(rest) = rest {
                if let Some(stamp) = rest.strip_suffix(".log") {
                    let stamp = match stamp.split_once('.') {
                        Some((timestamp, sequence)) if sequence.parse::<usize>().is_ok() => {
                            timestamp
                        }
                        Some(_) => continue,
                        None => stamp,
                    };
                    if let Ok(ts) = chrono::NaiveDateTime::parse_from_str(stamp, "%Y%m%d-%H%M%S") {
                        if chrono::Local
                            .from_local_datetime(&ts)
                            .single()
                            .is_some_and(|t| t < cutoff)
                            && std::fs::remove_file(entry.path()).is_ok()
                        {
                            removed += 1;
                        }
                    }
                }
            }
        }
    }
    removed
}
