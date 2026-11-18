import json
import sys
from ldap3 import Server, Connection, ALL, SUBTREE, LEVEL, BASE, ALL_ATTRIBUTES, ALL_OPERATIONAL_ATTRIBUTES, DEREF_ALWAYS

LDAP_SERVER = r"$CustomProperty.DCLDAPServer$"
LDAP_USERNAME = r"$EffectiveUsername$"
LDAP_PASSWORD = r"$EffectivePassword$"
BASE_DN = r"$CustomProperty.SearchBase$"
ADMIN_CRED = r"$CustomProperty.AdminCredential$"
USER_CRED = r"$CustomProperty.UserCredential$"

LDAP_CONNECTION_STRING = "ldaps://" + LDAP_SERVER
SEARCH_FILTER = "(&(objectCategory=computer)(objectClass=computer))"

ATTRIBUTE_CN = "cn"
ATTRIBUTE_DNS_HOSTNAME = "dNSHostName"
ATTRIBUTE_NETWORK_ADDRESS = "networkAddress"
ATTRIBUTE_OPERATING_SYSTEM = "operatingSystem"
ATTRIBUTES_TO_QUERY = [ATTRIBUTE_CN, ATTRIBUTE_DNS_HOSTNAME, ATTRIBUTE_NETWORK_ADDRESS, ATTRIBUTE_OPERATING_SYSTEM]

PAGE_SIZE = 1000

OS_WINDOWS = "windows"
OS_MACOS = "macos"
OS_LINUX = "linux"
OS_OTHER = "other"

OS_OBJECT_TYPE_MAPPING = {
    OS_WINDOWS: "RemoteDesktopConnection",
    OS_MACOS: "VNCConnection",
    OS_LINUX: "TerminalConnection",
    OS_OTHER: "RemoteDesktopConnection"
}

def create_connection(object_type, terminal_connection_type, name, host, path):
    connection = {
        "Type": object_type,
        "Name": name,
        "ComputerName": host,
        "CredentialName": ADMIN_CRED if is_domain_controllers(path) else USER_CRED
    }

    if path is not None and path != "":
        connection["Path"] = path

    return connection

def get_object_type(os):
    return OS_OBJECT_TYPE_MAPPING[os]

def get_os(operating_system):
    if operating_system is None:
        return OS_WINDOWS

    operating_system_lower = operating_system.lower()

    if operating_system_lower.startswith("windows"):
        return OS_WINDOWS
    elif operating_system_lower.startswith("mac os"):
        return OS_MACOS
    elif operating_system_lower.startswith("linux") or operating_system_lower.startswith("ubuntu") or operating_system_lower.startswith("centos") or operating_system_lower.startswith("debian"):
        return OS_LINUX
    else:
        return OS_OTHER

def get_path(dn, computer_cn):
    dn_arr = dn.split(",")
    path_arr = []

    for part in dn_arr:
        part_lower = part.lower()

        if part_lower.startswith("ou=") or part_lower.startswith("cn="):
            part_val_arr = part.split("=")

            if len(part_val_arr) > 1:
                part_val = part_val_arr[1]

                if computer_cn != part_val:
                    path_arr.append(part_val)

    path_arr.reverse()
    return "/".join(path_arr)

def is_domain_controllers(path):
    """Check if the computer is in a Domain Controllers OU"""
    return "Domain Controllers" in path

def get_ldap_result(ldap_connection_string, ldap_username, ldap_password, base_dn):
    try:
        # First try with LDAPS
        server = Server(ldap_connection_string, get_info=ALL)
        conn = Connection(server, user=ldap_username, password=ldap_password, auto_bind=True)
    except Exception as e:
        print(f"LDAPS connection failed: {str(e)}", file=sys.stderr)
        print("Trying standard LDAP connection...", file=sys.stderr)
        # Fallback to standard LDAP
        ldap_connection_string = "ldap://" + LDAP_SERVER
        server = Server(ldap_connection_string, get_info=ALL)
        conn = Connection(server, user=ldap_username, password=ldap_password, auto_bind=True)
    
    print(f"Successfully connected to {ldap_connection_string}", file=sys.stderr)
    
    # Enable paged search to handle large result sets
    entries = []
    
    # Search with paging
    cookie = None
    while True:
        conn.search(
            search_base=base_dn,
            search_filter=SEARCH_FILTER,
            search_scope=SUBTREE,
            attributes=ATTRIBUTES_TO_QUERY,
            paged_size=PAGE_SIZE,
            paged_cookie=cookie
        )
        
        entries.extend(conn.entries)
        
        # Get the cookie for the next page
        cookie = conn.result.get('controls', {}).get('1.2.840.113556.1.4.319', {}).get('value', {}).get('cookie')
        
        # If no cookie or empty cookie, we're done
        if not cookie:
            break
    
    print(f"Found {len(entries)} computer objects in Active Directory", file=sys.stderr)
    return entries

def ldap_entry_to_connection(entry):
    computer_cn = entry.cn.value if hasattr(entry, 'cn') else None
    
    path = get_path(entry.entry_dn, computer_cn) if computer_cn else ""
    
    dns_hostname = entry.dNSHostName.value if hasattr(entry, 'dNSHostName') else None
    network_address = entry.networkAddress.value if hasattr(entry, 'networkAddress') else None
    operating_system = entry.operatingSystem.value if hasattr(entry, 'operatingSystem') else None
    
    os = get_os(operating_system)
    object_type = get_object_type(os)
    terminal_connection_type = "SSHConnection"
    
    computer_name = dns_hostname if dns_hostname else network_address
    
    # If no DNS hostname or network address, use the CN as a fallback
    if not computer_name and computer_cn:
        computer_name = computer_cn
    
    connection = None
    if computer_name and computer_cn:
        connection = create_connection(object_type, terminal_connection_type, computer_cn, computer_name, path)
    
    return connection

def get_connections(ldap_connection_string, ldap_username, ldap_password, base_dn):
    entries = get_ldap_result(ldap_connection_string, ldap_username, ldap_password, base_dn)
    
    connections = []
    for entry in entries:
        connection = ldap_entry_to_connection(entry)
        if connection:
            connections.append(connection)
    
    # Sort connections by path first, then by name
    connections.sort(key=lambda x: (x.get("Path", ""), x.get("Name", "")))
    
    print(f"Created {len(connections)} connection objects", file=sys.stderr)
    return connections

original_stdout = sys.stdout
sys.stdout = sys.stderr

try:
    connections = get_connections(LDAP_CONNECTION_STRING, LDAP_USERNAME, LDAP_PASSWORD, BASE_DN)

    store = {
        "Objects": connections
    }

    # Restore stdout for JSON output only
    sys.stdout = original_stdout
    jsonStr = json.dumps(store)
    print(jsonStr)
except Exception as e:
    print(f"Error: {str(e)}", file=sys.stderr)
    print("Please check your LDAP server address and credentials.", file=sys.stderr)
    # Output empty JSON object to prevent parsing errors in Royal TS
    sys.stdout = original_stdout
    print('{"Objects": []}')
