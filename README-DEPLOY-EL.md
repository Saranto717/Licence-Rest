# License Manager Cloud — build και εγκατάσταση

## Πώς λειτουργεί η έκδοση

- Το `Dockerfile` χτίζει την εφαρμογή License Manager και το cloud API.
- Κάθε Pull Request προς το `main` κάνει Docker build για να ελεγχθεί ότι η εικόνα χτίζεται. Δεν δημοσιεύει εικόνα.
- Κάθε push στο `main` δημοσιεύει δοκιμαστικές εικόνες `:main` και `:sha-...`.
- Κάθε σταθερό GitHub tag `vMAJOR.MINOR.PATCH` χτίζει και δημοσιεύει την αντίστοιχη εικόνα (`:v1.0.2`) και το `:latest`.
- Το `docker-compose.yml` χρησιμοποιεί το `:latest`. Με ρυθμισμένο Dockhand webhook, η νέα έκδοση γίνεται pull και deploy μετά την επιτυχία του build.

Για κυκλοφορία, άνοιξε **GitHub → Releases → Draft a new release**, δημιούργησε tag όπως `v1.0.2` με target το `main` και δημοσίευσε το release. Το Actions ξεκινά όταν δημιουργηθεί το tag. Δεν αλλάζεις αριθμό έκδοσης μέσα στο Dockerfile.

Το Docker image είναι `ghcr.io/saranto717/restaurantorders-cloud-api`. Αν το GHCR package είναι private, πρόσθεσε στο Dockhand registry credential για το GHCR με δικαίωμα `read:packages`. Εναλλακτικά, άλλαξε την ορατότητα του package σε public από τις ρυθμίσεις του GitHub package.

## Πρώτη εγκατάσταση στο Dockhand

1. Στο Dockhand δημιούργησε **Git stack** από το `https://github.com/Saranto717/Licence-Rest`, branch `main`, compose file `docker-compose.yml`.
2. Πρόσθεσε τις μεταβλητές της ενότητας «Μεταβλητές Dockhand» παρακάτω. Σήμανε ως **Secret** τις τιμές που έχουν ένδειξη «Secret».
3. Ρύθμισε το image pull credential αν το GHCR package είναι private και κάνε Deploy.
4. Στις ρυθμίσεις του Git stack ενεργοποίησε **Webhook**, όρισε ένα μυστικό webhook, και ενεργοποίησε **Force redeployment** και **Re-pull images**. Αυτές οι δύο επιλογές χρειάζονται για να ξανατραβηχτεί το `:latest` όταν δεν έχει αλλάξει το compose file.
5. Αν θέλεις αυτόματο deploy μετά από release, πρόσθεσε στο GitHub repository `Saranto717/Licence-Rest` τα Actions repository secrets:

   - `DOCKHAND_WEBHOOK_URL`: το webhook URL που εμφανίζει το Dockhand για το stack.
   - `DOCKHAND_WEBHOOK_SECRET`: το ίδιο μυστικό που έβαλες στο Dockhand.

Χωρίς αυτά τα δύο Actions secrets, το image θα δημοσιεύεται κανονικά στο GHCR και μπορείς να κάνεις deploy πατώντας Deploy στο Dockhand.

## Μεταβλητές Dockhand

| Μεταβλητή | Τύπος | Χρήση |
|---|---|---|
| `POSTGRES_DB` | Απλή | Όνομα βάσης, π.χ. `restaurant_cloud` |
| `POSTGRES_ADMIN_USER` | Απλή | Διαχειριστικός χρήστης PostgreSQL |
| `POSTGRES_ADMIN_PASSWORD` | **Secret** | Κωδικός διαχειριστικού χρήστη PostgreSQL |
| `CLOUD_APP_USER` | Απλή | Χρήστης εφαρμογής PostgreSQL |
| `CLOUD_APP_PASSWORD` | **Secret** | Κωδικός χρήστη εφαρμογής PostgreSQL |
| `CLOUD_ADMIN_KEY` | **Secret** | Κλειδί για διαχείριση καταστημάτων από το API |
| `LICENSE_MANAGER_PASSWORD` | **Secret** | Κωδικός σύνδεσης στο License Manager |
| `LICENSE_SIGNING_PRIVATE_KEY_B64` | **Secret** | Υπάρχον ιδιωτικό RSA signing key σε Base64 |
| `CLOUD_API_BIND_ADDRESS` | Απλή | Διεύθυνση host του API, συνήθως `127.0.0.1` για proxy στον ίδιο VPS |
| `CLOUD_API_PORT` | Απλή | Τοπική θύρα API, προεπιλογή `8080` |

Βάλε διαφορετικά, ισχυρά μυστικά στα πεδία password και API key. Για τυχαία τιμή 32 bytes σε Linux:

```sh
openssl rand -hex 32
```

Το `LICENSE_SIGNING_PRIVATE_KEY_B64` πρέπει να είναι το ιδιωτικό κλειδί που αντιστοιχεί στο public key που περιλαμβάνει το POS. Αν το υπάρχον `.pem` βρίσκεται στον VPS, κωδικοποίησέ το σε μία γραμμή και βάλε το αποτέλεσμα ως Secret στο Dockhand:

```sh
base64 -w0 license-signing-private.pem
```

Μην δημιουργήσεις καινούριο signing key χωρίς να προστεθεί και το αντίστοιχο public key σε έκδοση του POS. Το ιδιωτικό κλειδί δεν μπαίνει στο GitHub repository ή στο Docker image.

Ο proxy πρέπει να προωθεί το HTTPS traffic στο `http://127.0.0.1:8080`. Αν τρέχει σε διαφορετικό host ή Docker network, βάλε στο `CLOUD_API_BIND_ADDRESS` διεύθυνση προσβάσιμη από τον proxy και περιόρισε την πρόσβαση με firewall. Μην εκθέσεις τη θύρα `5432` της PostgreSQL στο Internet.

Η PostgreSQL κρατά τα δεδομένα στο named volume `postgres-data`. Κράτα τακτικά αντίγραφα ασφαλείας του volume.

## License Manager

Άνοιξε `https://<domain>/license-manager`. Στο POS, η οθόνη άδειας εμφανίζει τον κωδικό εγκατάστασης. Συμπλήρωσε τον κωδικό, τον πελάτη και τη διάρκεια σε μήνες στον License Manager. Το POS ελέγχει αυτόματα τον server και περνά στην εφαρμογή μόλις εγκριθεί.

Από τη λίστα μπορείς να αλλάξεις στοιχεία πελάτη, ημερομηνία λήξης, όριο εγκαταστάσεων και σχόλια, να ανανεώσεις για μήνες, να ανακαλέσεις ή να διαγράψεις μια άδεια. Η διαγραφή είναι λογική ώστε να διατηρείται το ιστορικό ελέγχου.
