use anyhow::Result;
use serde::{Deserialize, Serialize};
use std::path::Path;

#[derive(Deserialize, Serialize)]
pub struct Credentials {
    pub id: String,
    pub password: String,
}

#[cfg(windows)]
mod imp {
    use anyhow::{Context, Result};
    use windows::core::HSTRING;
    use windows::Win32::Foundation::HLOCAL;
    use windows::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
    };

    fn blob_to_vec(data: CRYPT_INTEGER_BLOB) -> Vec<u8> {
        if data.pbData.is_null() || data.cbData == 0 {
            return Vec::new();
        }
        let slice = unsafe { std::slice::from_raw_parts(data.pbData, data.cbData as usize) };
        let v = slice.to_vec();
        unsafe {
            let _ = windows::Win32::Foundation::LocalFree(HLOCAL(data.pbData as *mut _));
        }
        v
    }

    pub fn protect(data: &[u8]) -> Result<Vec<u8>> {
        let desc = HSTRING::from("InvenAdClicker");
        unsafe {
            let mut out = CRYPT_INTEGER_BLOB::default();
            CryptProtectData(
                &CRYPT_INTEGER_BLOB {
                    cbData: data.len() as u32,
                    pbData: data.as_ptr() as *mut u8,
                },
                &desc,
                None,
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut out,
            )
            .context("CryptProtectData 실패")?;
            Ok(blob_to_vec(out))
        }
    }

    pub fn unprotect(data: &[u8]) -> Result<Vec<u8>> {
        unsafe {
            let mut out = CRYPT_INTEGER_BLOB::default();
            CryptUnprotectData(
                &CRYPT_INTEGER_BLOB {
                    cbData: data.len() as u32,
                    pbData: data.as_ptr() as *mut u8,
                },
                None,
                None,
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut out,
            )
            .context("CryptUnprotectData 실패 (다른 사용자/머신에서 보호된 파일?)")?;
            Ok(blob_to_vec(out))
        }
    }
}

#[cfg(windows)]
const FORMAT: &[u8] = b"InvenAdClicker:credentials:v6\0";

#[cfg(windows)]
pub fn load(dir: &Path) -> Result<Option<Credentials>> {
    let bytes = match std::fs::read(dir.join("credentials.dat")) {
        Ok(bytes) => bytes,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.into()),
    };
    // 구버전 파일에는 계정 정보가 없어 특정 아이디와 연결할 수 없다.
    let Some(encrypted) = bytes.strip_prefix(FORMAT) else {
        return Ok(None);
    };
    let pair: Credentials = serde_json::from_slice(&imp::unprotect(encrypted)?)?;
    Ok((!pair.id.trim().is_empty() && !pair.password.is_empty()).then_some(pair))
}

#[cfg(windows)]
pub fn save(dir: &Path, pair: &Credentials) -> Result<()> {
    use std::io::Write;
    let encrypted = imp::protect(&serde_json::to_vec(pair)?)?;
    let mut file = tempfile::NamedTempFile::new_in(dir)?;
    file.write_all(FORMAT)?;
    file.write_all(&encrypted)?;
    file.as_file().sync_all()?;
    file.persist(dir.join("credentials.dat"))?;
    Ok(())
}

#[cfg(not(windows))]
pub fn load(_: &Path) -> Result<Option<Credentials>> {
    Ok(None)
}
