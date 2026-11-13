# Πρώτη cloud υποδομή

Αυτό το stack ξεκινά το πρώτο backend του cloud: PostgreSQL 18, το cloud API και Caddy για HTTPS. Το GitHub Actions χτίζει το API image και το δημοσιεύει ιδιωτικά στο GitHub Container Registry (GHCR). Ο VPS κάνει pull από το GHCR. Η PostgreSQL δεν δημοσιεύει θύρα στο Internet. Το API δέχεται προσωρινά εγγραφές καταστημάτων και idempotent συμβάντα συγχρονισμού. Το τοπικό POS δεν στέλνει ακόμα δεδομένα· η σύνδεση του τοπικού outbox είναι το επόμενο στάδιο.

## Προϋποθέσεις

- VPS Linux με Docker Engine και Docker Compose plugin.
- Σταθερή δημόσια IPv4 διεύθυνση του VPS. Η πρόσβαση θα γίνεται απευθείας με `https://<IP>` και δεν απαιτείται domain ή DNS record.
- Ανοιχτές εισερχόμενες θύρες TCP 80 και 443 για το Caddy. Η θύρα 5432 δεν ανοίγει στο firewall.
- Διαθέσιμος χώρος δίσκου και ξεχωριστό σχέδιο αντιγράφων ασφαλείας εκτός του VPS.
- Το source repository έχει ανέβει στο GitHub και έχει εκτελεστεί επιτυχώς το workflow `Publish cloud API image`.

## GitHub Actions και GHCR

Το repository πρέπει να βρίσκεται στη ρίζα του GitHub repository, ώστε να αναγνωρίζεται το `.github/workflows/publish-cloud-image.yml`. Σε push αλλαγών στο cloud API, το GitHub Actions κάνει build και δημοσιεύει το image στο GHCR. Χρησιμοποιεί το προσωρινό `GITHUB_TOKEN` του workflow με `packages: write`· δεν χρειάζεται publish token μέσα στον κώδικα.

Το image παραμένει ιδιωτικό. Για το repository `Saranto717/Licence-Rest`, στο `.env` του VPS το `CLOUD_API_IMAGE` είναι `ghcr.io/saranto717/restaurantorders-cloud-api:latest`. Το Compose κάνει pull αυτό το image από το GHCR.

Για να κατεβάζει ο VPS ιδιωτικό image, δημιούργησε GitHub Personal Access Token (classic) με μόνο `read:packages` και δικαίωμα ανάγνωσης στο package. Σύνδεσε το Docker μία φορά με `docker login ghcr.io --username <username> --password-stdin`. Μη βάλεις το token στο repository, στο `.env` ή σε εντολή που το αποθηκεύει στο shell history. Το Docker το κρατά στα credentials του Linux χρήστη που εκτέλεσε το login. Οι οδηγίες του GitHub για [δημοσίευση με Actions](https://docs.github.com/en/packages/managing-github-packages-using-github-actions-workflows/publishing-and-installing-a-package-with-github-actions) και [δικαιώματα GHCR](https://docs.github.com/en/packages/learn-github-packages/about-permissions-for-github-packages) περιγράφουν τα permissions και τα token scopes.

## Εκκίνηση

1. Αντέγραψε το `.env.example` σε `.env`, βάλε τη δημόσια IPv4 στο `CLOUD_IP` και ένα email στο `ACME_EMAIL`. Το `CLOUD_API_IMAGE` είναι ήδη ρυθμισμένο για το GHCR package αυτού του repository. Πρόσθεσε τέσσερα διαφορετικά μυστικά, τουλάχιστον 32 bytes το καθένα. Σε Linux:

   ```sh
   test ! -e .env || { echo ".env υπάρχει ήδη· μην το αντικαταστήσεις."; exit 1; }
   cp .env.example .env
   for key in POSTGRES_PASSWORD_SECRET CLOUD_APP_PASSWORD_SECRET CLOUD_ADMIN_KEY_SECRET LICENSE_MANAGER_PASSWORD_SECRET; do
     printf '%s=%s\n' "$key" "$(openssl rand -hex 32)" >> .env
   done
   chmod 600 .env
   ```

   Έπειτα, ενημέρωσε τα `CLOUD_IP` και `ACME_EMAIL` μέσα στο `.env`. Το `.env` αγνοείται από το Git· μην το στείλεις μαζί με τον source κώδικα. Το Compose περνά τα μυστικά ως προστατευμένα αρχεία μέσα μόνο στα containers που τα χρειάζονται.
2. Δημιούργησε GitHub Personal Access Token (classic) με μόνο `read:packages` και δικαίωμα ανάγνωσης στο package. Συνδέσου στο GHCR από τον ίδιο Linux λογαριασμό που θα εκτελεί το Compose. Μην αποθηκεύσεις το token στο `.env` ή στο source:

   ```sh
   read -rsp "GitHub read:packages token: " GHCR_TOKEN; echo
   printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u YOUR_GITHUB_USERNAME --password-stdin
   unset GHCR_TOKEN
   ```

   Το GitHub Actions χρησιμοποιεί το προσωρινό `GITHUB_TOKEN` του workflow με δικαίωμα `packages: write` για να δημοσιεύει το image. Ο VPS χρησιμοποιεί το ξεχωριστό token ανάγνωσης `read:packages` για να το κατεβάζει.
3. Κατέβασε το image και ξεκίνα τα containers:

   ```sh
   docker compose --env-file .env pull cloud-api
   docker compose --env-file .env up -d
   ```

4. Έλεγξε το `https://<CLOUD_IP>/health/ready`. Πρέπει να επιστρέψει `{"status":"ready"}`.

## Δημοσίευση νέας έκδοσης

Μετά από push αλλαγών στο `cloud/RestaurantOrders.Cloud.Api` στο GitHub, το workflow χτίζει αυτόματα image με tag `latest` (στο default branch) και tag commit SHA. Από SSH στον VPS, μέσα στο `deploy/cloud`, εκτέλεσε:

```sh
docker compose --env-file .env pull cloud-api
docker compose --env-file .env up -d --no-deps cloud-api
```

Η σύνδεση με SSH γίνεται από εσένα προς τον VPS. Το Docker χρησιμοποιεί το αποθηκευμένο GHCR token για το pull. Για πλήρως αυτόματη ανάπτυξη μπορεί αργότερα να προστεθεί ξεχωριστό GitHub Actions SSH deploy job με περιορισμένο λογαριασμό VPS.

Η βάση αποθηκεύεται στο named volume `postgres-data`, στο `/var/lib/postgresql` που χρησιμοποιεί η επίσημη εικόνα PostgreSQL 18. Τα named volumes επιβιώνουν από αναδημιουργία container, αλλά χρειάζονται και εξωτερικά backups. Ο λογαριασμός `POSTGRES_ADMIN_USER` χρησιμοποιείται μόνο στην αρχικοποίηση και στους ελέγχους διαχείρισης. Το API συνδέεται με ξεχωριστό `CLOUD_APP_USER`, που έχει δικαιώματα μόνο στη βάση εφαρμογής.

Πριν ξεκινήσεις το API, δημιούργησε `secrets/` και αντέγραψε σε αυτό το `license-signing-private.pem` που αντιστοιχεί στο δημόσιο κλειδί ενσωματωμένο στην έκδοση του RestaurantOrders. Στον σταθμό ανάπτυξης το αρχικό ιδιωτικό κλειδί φυλάσσεται έξω από τον πηγαίο κώδικα στο `%LOCALAPPDATA%\RestaurantOrders\LicenseSecrets\license-signing-private.pem`. Μετέφερε το κλειδί στον VPS μόνο με ασφαλή τρόπο και όρισε δικαιώματα ανάγνωσης μόνο για τον διαχειριστή και το Docker. Μην το βάλεις στο `.env`, στο zip του source ή σε Git. Το API δεν εκκινεί χωρίς αυτό το κλειδί.

Το Caddy 2.10.1 ή νεότερο ζητά αυτόματα πιστοποιητικό HTTPS για την IP και το ανανεώνει. Τα πιστοποιητικά IP της Let's Encrypt είναι βραχύβια, περίπου 160 ώρες, οπότε η αυτόματη ανανέωση πρέπει να παραμένει ενεργή.

## Προσθήκη καταστήματος

Μετά την εκκίνηση, ο διαχειριστής δημιουργεί εγγραφή καταστήματος. Η απάντηση περιέχει το `apiToken` μόνο μία φορά· αποθήκευσέ το με ασφάλεια για την εγκατάσταση του συγκεκριμένου καταστήματος.

```sh
curl --fail-with-body \
  -H "X-Cloud-Admin-Key: $(cat secrets/cloud_admin_key)" \
  -H "Content-Type: application/json" \
  -d '{"name":"Κατάστημα Αθήνα"}' \
  https://<CLOUD_IP>/api/admin/v1/stores
```

Το endpoint συγχρονισμού είναι `POST /api/sync/v1/events` με `Authorization: Bearer <apiToken>`. Δέχεται 1–100 συμβάντα ανά αποστολή. Κάθε συμβάν έχει σταθερό `eventId`, `entityType`, `entityId`, `eventType`, `schemaVersion`, `occurredAtUtc` σε UTC και αντικείμενο `payload`. Επαναποστολή του ίδιου `eventId` για το ίδιο κατάστημα δεν δημιουργεί δεύτερη εγγραφή.

Αν χαθεί το token ενός καταστήματος, μπορεί να εκδοθεί νέο με `POST /api/admin/v1/stores/<StoreId>/rotate-token` και το ίδιο `X-Cloud-Admin-Key`. Η έκδοση νέου token ακυρώνει αμέσως το παλιό.

## License Manager

Άνοιξε `https://<CLOUD_IP>/license-manager` και βάλε την τιμή `LICENSE_MANAGER_PASSWORD_SECRET` από το `.env`. Η σύνδεση χρησιμοποιεί HTTPS και ο κωδικός δεν αποθηκεύεται στη σελίδα.

Ο installer του POS ζητά την ίδια διεύθυνση HTTPS. Μετά την πρώτη σύνδεση της εγκατάστασης, αυτή εμφανίζεται ως εκκρεμής στον License Manager. Δημιούργησε άδεια, ανάθεσέ την στην εγκατάσταση για ενεργοποίηση, και από την ίδια οθόνη μπορείς να την ανανεώσεις, να την ακυρώσεις ή να αποσυνδέσεις μια συσκευή. Οι αλλαγές εφαρμόζονται στην τοπική εγκατάσταση όταν επικοινωνήσει ξανά με το cloud. Το POS ελέγχει ανά 10 λεπτά και κρατά υπογεγραμμένη τοπική άδεια για έως 30 ημέρες χωρίς Internet, ώστε να συνεχίζει να λειτουργεί σε διακοπή σύνδεσης.

## Τρέχουσα έκταση

Η τοπική εφαρμογή εξακολουθεί να χρησιμοποιεί SQLite, ώστε να παραμένει διαθέσιμη χωρίς Internet. Οι απομακρυσμένες ακυρώσεις αδειών εφαρμόζονται μόλις η εγκατάσταση συνδεθεί με τον server· καμία εντολή από το Internet δεν μπορεί να φτάσει σε υπολογιστή που είναι αποσυνδεδεμένος. Δεν έχει προστεθεί ακόμη ουρά συμβάντων στην τοπική SQLite, συγχρονισμός καταλόγου/χρηστών, οθόνη cloud αναφορών ή προώθηση απομακρυσμένων εντολών προς εκτυπωτές και παρόχους.

Τα SQL migrations του API αποθηκεύονται στο `cloud/RestaurantOrders.Cloud.Api/Migrations`. Τα migrations εφαρμόζονται αυτόματα κατά την εκκίνηση του API.
