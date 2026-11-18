//! Active Directory: computer objects become connections.
//!
//! Same rules as the Royal TS dynamic folder script this replaces: paged
//! LDAP search for computers, operating system decides the protocol, the OU
//! chain becomes the folder path, and machines under a Domain Controllers
//! OU get the admin credential while everything else gets the user one.

use std::time::Duration;

use ldap3::adapters::{Adapter, EntriesOnly, PagedResults};
use ldap3::{LdapConnAsync, LdapConnSettings, Scope, SearchEntry};

use crate::error::CoreError;
use crate::store::{AuthKind, Connection, ConnectionKind, Credential, Provider};

const FILTER: &str = "(&(objectCategory=computer)(objectClass=computer))";
const ATTRS: [&str; 4] = ["cn", "dNSHostName", "networkAddress", "operatingSystem"];
const PAGE_SIZE: i32 = 1000;

#[derive(Debug, Clone)]
pub struct AdSettings {
    pub server: String,
    pub search_base: String,
    pub allow_plain_ldap: bool,
    pub accept_invalid_certs: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdComputer {
    pub dn: String,
    pub cn: String,
    pub dns_host_name: Option<String>,
    pub network_address: Option<String>,
    pub operating_system: Option<String>,
}

/// Queries every computer object under the search base. Tries LDAPS first
/// and, if allowed, falls back to plain LDAP with a warning.
pub async fn fetch_computers(
    settings: &AdSettings,
    username: &str,
    password: &str,
) -> Result<(Vec<AdComputer>, Vec<String>), CoreError> {
    let server = settings.server.trim();
    if server.is_empty() {
        return Err(CoreError::Invalid("LDAP server is required".into()));
    }
    if settings.search_base.trim().is_empty() {
        return Err(CoreError::Invalid("search base is required".into()));
    }
    let mut urls = vec![format!("ldaps://{server}")];
    if settings.allow_plain_ldap {
        urls.push(format!("ldap://{server}"));
    }
    let mut warnings = Vec::new();
    for (index, url) in urls.iter().enumerate() {
        match query(url, settings, username, password).await {
            Ok(computers) => {
                if index > 0 {
                    warnings.push(format!(
                        "LDAPS failed ({}); fell back to unencrypted {url}, so the bind password was sent in the clear.",
                        warnings.join("; ")
                    ));
                }
                return Ok((computers, warnings));
            }
            Err(e) => warnings.push(format!("{url}: {e}")),
        }
    }
    Err(CoreError::Connect(format!(
        "could not query Active Directory: {}",
        warnings.join("; ")
    )))
}

async fn query(url: &str, settings: &AdSettings, username: &str, password: &str) -> Result<Vec<AdComputer>, CoreError> {
    let conn_settings = LdapConnSettings::new()
        .set_conn_timeout(Duration::from_secs(15))
        .set_no_tls_verify(settings.accept_invalid_certs);
    let (conn, mut ldap) = LdapConnAsync::with_settings(conn_settings, url)
        .await
        .map_err(|e| CoreError::Connect(e.to_string()))?;
    ldap3::drive!(conn);

    ldap.simple_bind(username, password)
        .await
        .map_err(|e| CoreError::Connect(e.to_string()))?
        .success()
        .map_err(|e| CoreError::Auth(format!("LDAP bind as {username} failed: {e}")))?;

    // The PagedResults adapter follows the server's paging cookies for us.
    let adapters: Vec<Box<dyn Adapter<_, _>>> =
        vec![Box::new(EntriesOnly::new()), Box::new(PagedResults::new(PAGE_SIZE))];
    let mut search = ldap
        .streaming_search_with(adapters, settings.search_base.trim(), Scope::Subtree, FILTER, &ATTRS)
        .await
        .map_err(|e| CoreError::Connect(format!("LDAP search failed: {e}")))?;
    let mut computers = Vec::new();
    while let Some(entry) = search
        .next()
        .await
        .map_err(|e| CoreError::Connect(format!("LDAP search failed: {e}")))?
    {
        computers.push(parse_entry(SearchEntry::construct(entry)));
    }
    search
        .finish()
        .await
        .success()
        .map_err(|e| CoreError::Connect(format!("LDAP search failed: {e}")))?;
    let _ = ldap.unbind().await;
    Ok(computers)
}

fn parse_entry(entry: SearchEntry) -> AdComputer {
    let first = |name: &str| -> Option<String> {
        entry
            .attrs
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .and_then(|(_, v)| v.first().cloned())
            .filter(|v| !v.is_empty())
    };
    AdComputer {
        cn: first("cn").unwrap_or_default(),
        dns_host_name: first("dNSHostName"),
        network_address: first("networkAddress"),
        operating_system: first("operatingSystem"),
        dn: entry.dn,
    }
}

/// Protocol for a machine from its `operatingSystem` attribute.
pub fn os_kind(operating_system: Option<&str>) -> ConnectionKind {
    let Some(os) = operating_system else {
        return ConnectionKind::Rdp;
    };
    let os = os.trim().to_lowercase();
    const SSH_PREFIXES: [&str; 13] = [
        "linux", "ubuntu", "centos", "debian", "red hat", "rhel", "rocky", "alma", "suse", "fedora",
        "freebsd", "nutanix", "ahv",
    ];
    if os.starts_with("windows") {
        ConnectionKind::Rdp
    } else if os.starts_with("mac os") || os.starts_with("macos") {
        ConnectionKind::Vnc
    } else if SSH_PREFIXES.iter().any(|p| os.starts_with(p)) {
        ConnectionKind::Ssh
    } else {
        ConnectionKind::Rdp
    }
}

/// Folder path from a DN: every OU and CN above the computer itself, root
/// first, joined with `/`.
pub fn ou_path(dn: &str, cn: &str) -> String {
    let mut parts: Vec<String> = Vec::new();
    for component in split_dn(dn) {
        let Some((attr, value)) = component.split_once('=') else { continue };
        let attr = attr.trim().to_lowercase();
        if attr != "ou" && attr != "cn" {
            continue;
        }
        let value = value.trim();
        if value.eq_ignore_ascii_case(cn) {
            continue;
        }
        parts.push(value.to_string());
    }
    parts.reverse();
    parts.join("/")
}

/// Splits a DN on unescaped commas.
fn split_dn(dn: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut current = String::new();
    let mut escaped = false;
    for ch in dn.chars() {
        if escaped {
            current.push(ch);
            escaped = false;
        } else if ch == '\\' {
            escaped = true;
        } else if ch == ',' {
            parts.push(std::mem::take(&mut current));
        } else {
            current.push(ch);
        }
    }
    if !current.is_empty() {
        parts.push(current);
    }
    parts
}

pub fn is_domain_controllers(path: &str) -> bool {
    path.contains("Domain Controllers")
}

pub fn to_connections(
    computers: &[AdComputer],
    provider: &Provider,
    admin: Option<&Credential>,
    user: Option<&Credential>,
) -> Vec<Connection> {
    computers
        .iter()
        .filter(|c| !c.cn.is_empty())
        .map(|computer| {
            let path = ou_path(&computer.dn, &computer.cn);
            let credential = if is_domain_controllers(&path) {
                admin.or(user)
            } else {
                user.or(admin)
            };
            let kind = os_kind(computer.operating_system.as_deref());
            let host = computer
                .dns_host_name
                .clone()
                .or_else(|| computer.network_address.clone())
                .unwrap_or_else(|| computer.cn.clone());
            let folder = if path.is_empty() {
                provider.folder.clone()
            } else {
                format!("{}/{}", provider.folder, path)
            };
            let mut tags = vec!["ad".to_string()];
            if let Some(os) = &computer.operating_system {
                tags.push(os.clone());
            }
            Connection {
                id: format!("{}:{}", provider.id, computer.dn.to_lowercase()),
                name: computer.cn.clone(),
                kind,
                host,
                port: kind.default_port(),
                username: credential.map(|c| c.username.clone()).unwrap_or_default(),
                auth: match credential {
                    Some(c) => AuthKind::Credential { credential_id: c.id.clone() },
                    None => AuthKind::Password,
                },
                folder: Some(folder),
                tags,
                source: Some(provider.id.clone()),
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::store::{CredentialKind, ProviderKind};

    #[test]
    fn maps_operating_systems() {
        assert_eq!(os_kind(Some("Windows Server 2022 Datacenter")), ConnectionKind::Rdp);
        assert_eq!(os_kind(Some("Mac OS X")), ConnectionKind::Vnc);
        assert_eq!(os_kind(Some("Ubuntu 22.04")), ConnectionKind::Ssh);
        assert_eq!(os_kind(Some("Red Hat Enterprise Linux")), ConnectionKind::Ssh);
        assert_eq!(os_kind(Some("Nutanix AHV")), ConnectionKind::Ssh);
        assert_eq!(os_kind(Some("Something Else")), ConnectionKind::Rdp);
        assert_eq!(os_kind(None), ConnectionKind::Rdp);
    }

    #[test]
    fn builds_folder_path_from_dn() {
        assert_eq!(
            ou_path("CN=DC01,OU=Domain Controllers,DC=corp,DC=example", "DC01"),
            "Domain Controllers"
        );
        assert_eq!(
            ou_path("CN=WS42,OU=Workstations,OU=Office,DC=corp,DC=example", "WS42"),
            "Office/Workstations"
        );
        assert_eq!(ou_path("CN=PC1,CN=Computers,DC=corp,DC=example", "PC1"), "Computers");
        assert_eq!(ou_path("CN=Odd\\, Name,OU=Lab,DC=x", "Odd, Name"), "Lab");
    }

    fn cred(id: &str, user: &str) -> Credential {
        Credential {
            id: id.into(),
            name: id.into(),
            username: user.into(),
            kind: CredentialKind::Vault,
            folder: None,
            has_private_key: false,
            tags: vec![],
            source: None,
        }
    }

    fn provider() -> Provider {
        Provider {
            id: "ad1".into(),
            name: "AD".into(),
            kind: ProviderKind::ActiveDirectory {
                server: "dc01".into(),
                search_base: "DC=corp,DC=example".into(),
                bind_credential_id: None,
                admin_credential_id: Some("admin".into()),
                user_credential_id: Some("user".into()),
                allow_plain_ldap: true,
                accept_invalid_certs: false,
            },
            folder: "Active Directory".into(),
            auto_refresh_minutes: 0,
            last_sync_epoch: None,
            last_status: None,
        }
    }

    #[test]
    fn assigns_credentials_hosts_and_folders() {
        let computers = vec![
            AdComputer {
                dn: "CN=DC01,OU=Domain Controllers,DC=corp,DC=example".into(),
                cn: "DC01".into(),
                dns_host_name: Some("dc01.corp.example".into()),
                network_address: None,
                operating_system: Some("Windows Server 2022".into()),
            },
            AdComputer {
                dn: "CN=nutanix1,OU=Lab,DC=corp,DC=example".into(),
                cn: "nutanix1".into(),
                dns_host_name: None,
                network_address: Some("192.0.2.50".into()),
                operating_system: Some("Linux".into()),
            },
            AdComputer {
                dn: "CN=orphan,DC=corp,DC=example".into(),
                cn: "orphan".into(),
                dns_host_name: None,
                network_address: None,
                operating_system: None,
            },
            AdComputer {
                dn: "CN=,DC=x".into(),
                cn: String::new(),
                dns_host_name: None,
                network_address: None,
                operating_system: None,
            },
        ];
        let admin = cred("admin", "CORP\\admin");
        let user = cred("user", "CORP\\jdoe");
        let out = to_connections(&computers, &provider(), Some(&admin), Some(&user));
        assert_eq!(out.len(), 3);

        let dc = &out[0];
        assert_eq!(dc.kind, ConnectionKind::Rdp);
        assert_eq!(dc.port, 3389);
        assert_eq!(dc.host, "dc01.corp.example");
        assert_eq!(dc.username, "CORP\\admin");
        assert_eq!(dc.auth, AuthKind::Credential { credential_id: "admin".into() });
        assert_eq!(dc.folder.as_deref(), Some("Active Directory/Domain Controllers"));
        assert_eq!(dc.source.as_deref(), Some("ad1"));

        let nutanix = &out[1];
        assert_eq!(nutanix.kind, ConnectionKind::Ssh);
        assert_eq!(nutanix.port, 22);
        assert_eq!(nutanix.host, "192.0.2.50");
        assert_eq!(nutanix.auth, AuthKind::Credential { credential_id: "user".into() });
        assert_eq!(nutanix.folder.as_deref(), Some("Active Directory/Lab"));

        let orphan = &out[2];
        assert_eq!(orphan.host, "orphan");
        assert_eq!(orphan.folder.as_deref(), Some("Active Directory"));

        // Missing credentials degrade to prompting.
        let none = to_connections(&computers[..1], &provider(), None, None);
        assert_eq!(none[0].auth, AuthKind::Password);
        assert_eq!(none[0].username, "");
    }

    #[tokio::test]
    async fn refuses_blank_settings_before_connecting() {
        let settings = AdSettings {
            server: " ".into(),
            search_base: "DC=x".into(),
            allow_plain_ldap: false,
            accept_invalid_certs: false,
        };
        assert!(matches!(fetch_computers(&settings, "u", "p").await, Err(CoreError::Invalid(_))));
    }
}
