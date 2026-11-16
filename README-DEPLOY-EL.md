# Cloud deployment

## Ροή έκδοσης

- Το `Dockerfile` στη ρίζα χτίζει μόνο το cloud API.
- Το GitHub Actions χτίζει το Docker image και το δημοσιεύει στο GHCR.
- Το `docker-compose.yml` στη ρίζα δεν κάνει build· κάνει pull το image από το GHCR και ξεκινά το API μαζί με την PostgreSQL.
- Push στο `main` φτιάχνει δοκιμαστικό image με tags `main` και commit SHA.
- Git tag έκδοσης όπως `v17.3.113` δημιουργεί τα images `:v17.3.113` και `:latest`. Με ενεργό deployment, το Actions συνδέεται μέσω SSH και αναβαθμίζει τον VPS με το `:latest`.

Κυκλοφόρησε νέα έκδοση από commit του `main`:

```sh
git tag v17.3.113
git push origin v17.3.113
```

Ο αριθμός έκδοσης μπαίνει στο Git tag, όχι στην έκδοση .NET του Dockerfile. Το `latest` σημαίνει την τελευταία έκδοση που προωθήθηκε με version tag.

## Proxy

Δεν περιλαμβάνεται Caddy. Το Compose δημοσιεύει το API από προεπιλογή μόνο στο `127.0.0.1:8080` του VPS. Ο proxy του IT πρέπει να τερματίζει το HTTPS και να προωθεί τα αιτήματα στο `http://127.0.0.1:8080`. Το δημόσιο URL του proxy είναι η διεύθυνση που χρησιμοποιούν ο installer και ο License Manager.

Αν ο proxy τρέχει σε Docker ή σε διαφορετικό server, ρύθμισε πρώτα την προσβάσιμη ιδιωτική διεύθυνση του API και το firewall. Μην ανοίξεις τη θύρα 5432 της PostgreSQL στο Internet.

## Εγκατάσταση και αυτόματες ενημερώσεις με Dockhand

Το GitHub Actions χτίζει εικόνα για κάθε release tag τύπου `vX.Y.Z` και ανεβάζει την ίδια έκδοση με tag `latest` στο GHCR. Το Compose χρησιμοποιεί αυτό το `latest` tag, οπότε το Dockhand μπορεί να ενημερώνει το stack χωρίς αλλαγή branch.

Στο Git stack `cloud` του Dockhand:

1. Άφησε το branch στο `main` και απενεργοποίησε το **Build images on deploy**.
2. Ενεργοποίησε το **Enable scheduled sync** για το συγκεκριμένο stack.
3. Στις επιλογές deploy ενεργοποίησε **Re-pull images** και **Force redeployment**. Έτσι, όταν συγχρονίζεται το ίδιο commit, το Dockhand ξανατραβά το `latest` και εφαρμόζει τη νέα εικόνα.
4. Ρύθμισε το `CLOUD_IMAGE_TAG` σε `latest`.
5. Αν το GHCR package είναι ιδιωτικό, πρόσθεσε τα στοιχεία ανάγνωσης του GHCR στο Dockhand Registry.

Για να εκδοθεί ενημέρωση, φτιάξε GitHub Release με tag όπως `v1.0.2` από commit που υπάρχει στο `main`. Το GitHub Actions θα χτίσει και θα δημοσιεύσει τις εικόνες `v1.0.2` και `latest`. Το προγραμματισμένο sync του Dockhand θα τραβήξει το `latest` στην επόμενη εκτέλεση. Δεν αλλάζεις branch για κάθε έκδοση.

Στο Dockhand κράτησε τα παρακάτω ως μεταβλητές/μυστικά του stack. Το `LICENSE_SIGNING_PRIVATE_KEY_B64` πρέπει να είναι Base64 ολόκληρου του υπάρχοντος RSA private-key PEM. Κράτησε το ίδιο κλειδί στις επόμενες εκδόσεις, ώστε να παραμένουν έγκυρες οι άδειες που έχουν ήδη υπογραφεί.

- `POSTGRES_DB`
- `POSTGRES_ADMIN_USER`
- `POSTGRES_ADMIN_PASSWORD`
- `CLOUD_APP_USER`
- `CLOUD_APP_PASSWORD`
- `CLOUD_ADMIN_KEY`
- `LICENSE_MANAGER_PASSWORD`
- `LICENSE_SIGNING_PRIVATE_KEY_B64`
- `CLOUD_IMAGE_TAG=latest`

Η βάση μένει στο Docker volume `postgres-data` και διατηρείται σε αναβαθμίσεις container. Κράτα ξεχωριστά αντίγραφα ασφαλείας.

