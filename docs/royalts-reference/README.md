# Royal TSX reference scripts

The dynamic folder scripts Toolbox's providers replace. Kept verbatim so the
behaviour can be compared; the `$CustomProperty.*$` tokens were Royal TS
placeholders.

- `onepassword_common.py`: the 1Password script. The dynamic folder variant
  ends by calling `get_items()` (`op item list --format=json` per vault
  filter, converted to DynamicCredential objects grouped by vault); the
  dynamic credential variant ends by calling `get_item_details(item_id)`
  (`op item get <id>` and extraction of username / password / private key).
- `ad_computers.py`: dynamic folder script. LDAP(S) paged search for computer
  objects, OS → connection type, OU chain → folder path, Domain Controllers
  OU → admin credential, everything else → user credential.

Royal TS configuration that was in use (from the settings screenshots):
OP path (macOS) `/opt/homebrew/bin/op`, account filter `work`, vault filter
`work`. The AD folder's bind credential was a regular domain account
(`DOMAIN\user`).
