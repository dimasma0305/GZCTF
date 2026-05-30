import {
  Accordion,
  Anchor,
  Button,
  Code,
  CopyButton,
  Divider,
  Group,
  List,
  Modal,
  ModalProps,
  ScrollArea,
  Stack,
  Text,
  ThemeIcon,
  Title,
} from '@mantine/core'
import { showNotification } from '@mantine/notifications'
import {
  mdiCheck,
  mdiContentCopy,
  mdiCounter,
  mdiCrown,
  mdiHeartPulse,
  mdiRefresh,
  mdiTimerSandComplete,
  mdiToolboxOutline,
} from '@mdi/js'
import { Icon } from '@mdi/react'
import { FC } from 'react'
import { useTranslation } from 'react-i18next'
import { useAdToken, AdTokenSection, AdVpnSection, AdTokenRevealModal } from '@Components/AdToolkitSections'
import { KothHillList } from '@Components/KothHillList'
import misc from '@Styles/Misc.module.css'

interface KothToolkitModalProps extends ModalProps {
  gameId: number
}

/**
 * Player-facing toolkit for King of the Hill. Same shape as
 * <see cref="AdGuideModal"/> — actionable sections (token, VPN) on top,
 * reference docs underneath — but the KotH-specific accordion items are
 * different from A&D: the round-token endpoint + plant flow + state
 * lookup + hold/penalty scoring + 5-tick refresh + leader cooldown.
 *
 * The API token + VPN config are SHARED with A&D (one Bearer token covers
 * both engines, one WG tunnel reaches both bridges) — so this modal calls
 * the same endpoints as AdGuideModal for those sections rather than
 * duplicating state.
 */
export const KothGuideModal: FC<KothToolkitModalProps> = ({ gameId, ...modalProps }) => {
  const { t } = useTranslation()
  const { adTokenHint, rotating, freshToken, storedToken, forgetToken, tokenModalOpen, closeTokenModal, onRotate } = useAdToken(gameId, () =>
    showNotification({
      color: 'teal',
      message: t('game.notification.koth.token.rotated', 'KotH token rotated'),
      icon: <Icon path={mdiCheck} size={1} />,
    })
  )

  const apiUrl = `${typeof window !== 'undefined' ? window.location.origin : ''}/api/Game/${gameId}/Ad`
  const exampleBearer = freshToken ?? '<your-token>'

  const tokenCurlExample = [
    `curl -sS ${apiUrl}/Koth/<challenge-id>/Token \\`,
    `  -H "Authorization: Bearer ${exampleBearer}"`,
  ].join('\n')

  const tokenResponseExample = `{
  "round": 42,
  "token": "koth_QLDKIv_lrYms9knrtVafwF9gr7DRFoUF",
  "status": "ready"
}`

  const stateCurlExample = [
    `curl -sS ${apiUrl}/Koth/<challenge-id>/State \\`,
    `  -H "Authorization: Bearer ${exampleBearer}"`,
  ].join('\n')

  const stateResponseExample = `{
  "round": 42,
  "holderParticipationId": 1,
  "holderTeamName": "Team Alpha",
  "isYou": true,
  "status": "Ok",
  "checkedAt": "2026-05-28T11:25:42.123Z",
  "lastRefreshRound": 40
}`

  const targetsCurlExample = [
    `curl -sS ${apiUrl}/Targets \\`,
    `  -H "Authorization: Bearer ${exampleBearer}" \\`,
    `  | jq '.challenges[] | select(.hill) | {id: .challengeId, ip: .hill.ip, port: .hill.port}'`,
  ].join('\n')

  // Reference plant — players write their platform-issued control token verbatim
  // into /koth/king on the hill. The token is game-wide (works on every hill), so
  // <challenge-id> below is just any hill in this game. The exact write path depends
  // on the challenge (HTTP PUT, file write via exploit, raw socket, etc) — this is
  // just the shape players need to produce.
  const plantPseudocode = `# pseudocode — the actual write path depends on the hill's exploit
TOKEN=$(curl -sS ${apiUrl}/Koth/<challenge-id>/Token \\
  -H "Authorization: Bearer <your-token>" | jq -r '.token')

# exploit the hill so that this byte string ends up in /koth/king
write_to_hill "/koth/king" "$TOKEN"`

  return (
    <>
      <Modal
        size="48rem"
        centered
        title={
          <Group gap="sm">
            <ThemeIcon variant="light" color="violet" size="lg">
              <Icon path={mdiToolboxOutline} size={1} />
            </ThemeIcon>
            <Title order={4}>
              {t('game.content.koth.guide.title', 'King of the Hill — Toolkit')}
            </Title>
          </Group>
        }
        {...modalProps}
      >
        <ScrollArea h="70vh" scrollbarSize={6}>
          <Stack gap="md" pr="sm">
            <Text size="sm" c="dimmed">
              {t(
                'game.content.koth.guide.intro',
                'Everything you need to play King of the Hill: your API token, the VPN config, the control-token endpoint, and the rules. KotH shares the API token + VPN with A&D — one token, one tunnel, both engines.'
              )}
            </Text>

            <Accordion variant="separated" defaultValue={['token', 'vpn', 'hill']} radius="md" chevronPosition="left" multiple>
              {/* TOKEN — shared with A&D (see AdToolkitSections) */}
              <AdTokenSection
                hint={adTokenHint}
                rotating={rotating}
                onRotate={onRotate}
                storedToken={storedToken}
                onForget={forgetToken}
                title={t('game.content.koth.guide.token.title', 'Your API token')}
                intro={t(
                  'game.content.koth.guide.token.intro',
                  'A personal Bearer token scoped to you + this game. KotH and A&D share it — the same ad_… string authenticates both /Koth/{id}/Token and /Submit.'
                )}
                currentLabel={t('game.content.koth.guide.token.current', 'Your current token')}
              />

              {/* VPN — shared with A&D (see AdToolkitSections) */}
              <AdVpnSection
                gameId={gameId}
                title={t('game.content.koth.guide.vpn.title', 'VPN config')}
                intro={t(
                  'game.content.koth.guide.vpn.intro',
                  'Per-user WireGuard config. KotH hills live on the same bridges as A&D services — one tunnel reaches everything. The first download generates a fresh keypair + assigns you an IP from the game subnet; subsequent downloads return the same file.'
                )}
                linuxHint={t(
                  'game.content.koth.guide.vpn.linux_hint',
                  'Linux: sudo wg-quick up ./ad-game-….conf. macOS / Windows: import via the official WireGuard app.'
                )}
              />

              {/* HILL — KotH-specific: round token + plant flow */}
              <Accordion.Item value="hill">
                <Accordion.Control
                  icon={<Icon path={mdiCrown} size={1} color="var(--mantine-color-violet-6)" />}
                >
                  <Text fw={600}>{t('game.content.koth.guide.hill.title', 'Take the hill')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="sm">
                    <Text size="sm">
                      {t(
                        'game.content.koth.guide.hill.intro',
                        'A KotH challenge is a SINGLE shared container — the hill. Every team races to write their control token into the marker file /koth/king. Whichever token is in the marker when the checker reads it = the holder for that tick. Your token is GAME-WIDE — the same value works on every hill in this game.'
                      )}
                    </Text>
                    <Text size="sm" fw={600}>
                      {t('game.content.koth.guide.hill.step1', '1. Get your control token')}
                    </Text>
                    <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                      {tokenCurlExample}
                    </Code>
                    <Group justify="space-between">
                      <Text size="xs" c="dimmed">
                        {t(
                          'game.content.koth.guide.hill.token_note',
                          'Your token is GAME-WIDE (the same value works on every hill) and only rotates when the hills reset, every few ticks — fetch it once after a reset and plant it on whichever hills you take. A token from a previous window stops counting once the hills reset.'
                        )}
                      </Text>
                      <CopyButton value={tokenCurlExample}>
                        {({ copied, copy }) => (
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            leftSection={<Icon path={mdiContentCopy} size={0.7} />}
                            onClick={copy}
                          >
                            {copied
                              ? t('game.tooltip.copy.copied', 'Copied')
                              : t('game.button.ad.copy_curl', 'Copy curl')}
                          </Button>
                        )}
                      </CopyButton>
                    </Group>
                    <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                      {tokenResponseExample}
                    </Code>
                    <Text size="xs" c="dimmed">
                      {t(
                        'game.content.koth.guide.hill.status_note',
                        'status is "ready" / "warmup" (no round yet) / "no-token-this-round" (you joined after the mint; resolves next tick).'
                      )}
                    </Text>

                    <Divider />

                    <Text size="sm" fw={600}>
                      {t('game.content.koth.guide.hill.step2', '2. Plant it on the hill')}
                    </Text>
                    <Text size="sm">
                      {t(
                        'game.content.koth.guide.hill.plant_intro',
                        'How you get the bytes into /koth/king is the actual KotH challenge — exploit the hill’s service to write the file. The platform doesn’t care HOW you got it there, only what’s there when the checker peeks.'
                      )}
                    </Text>
                    <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                      {plantPseudocode}
                    </Code>
                    <Text size="xs" c="dimmed">
                      {t(
                        'game.content.koth.guide.hill.last_write_wins',
                        'Last write before the jittered check fires wins — plant LATE in the tick and a competing team can’t overwrite you. Plant EARLY and someone might.'
                      )}
                    </Text>

                    <Divider />

                    <Text size="sm" fw={600}>
                      {t('game.content.koth.guide.hill.step3', '3. Find the hill IP:port')}
                    </Text>
                    <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                      {targetsCurlExample}
                    </Code>
                    <Text size="xs" c="dimmed">
                      {t(
                        'game.content.koth.guide.hill.targets_note',
                        'No need to look up ids — the “Did my plant take?” section below lists every hill’s live IP:port (just click to copy). The hill IP changes every 5 ticks when the container is wiped + redeployed, so re-check it (or re-read /Targets) if your last-known IP stops responding.'
                      )}
                    </Text>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>

              {/* STATE — current holder + verdict */}
              <Accordion.Item value="state">
                <Accordion.Control
                  icon={<Icon path={mdiHeartPulse} size={1} color="var(--mantine-color-blue-6)" />}
                >
                  <Text fw={600}>{t('game.content.koth.guide.state.title', 'Did my plant take?')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="sm">
                    <Text size="sm">
                      {t(
                        'game.content.koth.guide.state.intro',
                        'Every hill in the game, live — name, target IP:port, who holds it right now, and the latest functional verdict. Confirm a plant landed without waiting for the next scoreboard refresh, and grab hill addresses without looking up challenge ids.'
                      )}
                    </Text>

                    {/* Live, ID-free list of all hills — the primary "did my plant take?" view. */}
                    <KothHillList gameId={gameId} />

                    <Divider
                      label={t('game.content.koth.guide.state.api_divider', 'Prefer the API? Query one hill by id:')}
                      labelPosition="center"
                    />
                    <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                      {stateCurlExample}
                    </Code>
                    <Group justify="flex-end">
                      <CopyButton value={stateCurlExample}>
                        {({ copied, copy }) => (
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            leftSection={<Icon path={mdiContentCopy} size={0.7} />}
                            onClick={copy}
                          >
                            {copied
                              ? t('game.tooltip.copy.copied', 'Copied')
                              : t('game.button.ad.copy_curl', 'Copy curl')}
                          </Button>
                        )}
                      </CopyButton>
                    </Group>
                    <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                      {stateResponseExample}
                    </Code>
                    <Text size="xs" c="dimmed">
                      {t(
                        'game.content.koth.guide.state.fields',
                        'isYou is true when YOUR team is the current holder. status is the functional probe verdict (Ok / Mumble / Offline / InternalError). lastRefreshRound is when the container was last wiped — round - lastRefreshRound tells you ticks remaining until the next wipe. The list above also exposes GET .../Ad/Koth/Hills for every hill at once.'
                      )}
                    </Text>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>

              {/* SCORING — KotH formulas */}
              <Accordion.Item value="scoring">
                <Accordion.Control
                  icon={<Icon path={mdiCounter} size={1} color="var(--mantine-color-teal-6)" />}
                >
                  <Text fw={600}>{t('game.content.koth.guide.scoring.title', 'How scoring works')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="sm">
                    <Text size="sm">
                      {t(
                        'game.content.koth.guide.scoring.intro',
                        'Per-tick deltas for whichever team holds the marker that tick:'
                      )}
                    </Text>
                    <List size="sm" spacing="xs">
                      <List.Item icon={<Text c="teal" fw={700} ff="monospace">+1</Text>}>
                        <Text component="span" fw={600} c="teal">
                          {t('game.content.koth.guide.scoring.hold_label', 'Hold credit')}:
                        </Text>{' '}
                        <Code className={misc.ffmono}>+KothHoldPointsPerTick</Code>{' '}
                        {t(
                          'game.content.koth.guide.scoring.hold',
                          '(default 1.0) per tick you hold the hill AND the hill’s functional probe is Ok. Flat — does NOT scale with team count.'
                        )}
                      </List.Item>
                      <List.Item icon={<Text c="red" fw={700} ff="monospace">−1</Text>}>
                        <Text component="span" fw={600} c="red">
                          {t('game.content.koth.guide.scoring.penalty_label', 'Broken-hill penalty')}:
                        </Text>{' '}
                        <Code className={misc.ffmono}>−KothBrokenHillPenalty</Code>{' '}
                        {t(
                          'game.content.koth.guide.scoring.penalty',
                          '(default 1.0) per tick you hold a broken hill (Mumble / Offline / Corrupt / InternalError). You broke the box you’re holding — fix it.'
                        )}
                      </List.Item>
                      <List.Item icon={<Icon path={mdiCheck} size={0.8} color="var(--mantine-color-gray-6)" />}>
                        <Text component="span" fw={600}>
                          {t('game.content.koth.guide.scoring.grace_label', 'Freshly-elected grace')}:
                        </Text>{' '}
                        {t(
                          'game.content.koth.guide.scoring.grace',
                          'first tick after you take over is exempt from the broken-hill penalty (the previous holder may have broken it — you didn’t have time to fix it yet). Holding into a second tick puts you on the hook.'
                        )}
                      </List.Item>
                    </List>
                    <Divider />
                    <Text size="sm" fw={600}>
                      {t('game.content.koth.guide.scoring.total_formula', 'Total = Σ (hold credit − penalty) across every tick you held')}
                    </Text>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>

              {/* REFRESH + COOLDOWN */}
              <Accordion.Item value="refresh">
                <Accordion.Control
                  icon={<Icon path={mdiTimerSandComplete} size={1} color="var(--mantine-color-blue-6)" />}
                >
                  <Text fw={600}>{t('game.content.koth.guide.refresh.title', '5-tick refresh + leader cooldown')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <List size="sm" spacing={4}>
                    <List.Item icon={<Icon path={mdiRefresh} size={0.8} color="var(--mantine-color-blue-6)" />}>
                      {t(
                        'game.content.koth.guide.refresh.wipe',
                        'The hill is wiped + redeployed every KothRefreshTicks rounds (default 5). Footholds, patches, planted markers — all gone. You must re-exploit the fresh box to start planting again.'
                      )}
                    </List.Item>
                    <List.Item icon={<Icon path={mdiCrown} size={0.8} color="var(--mantine-color-violet-6)" />}>
                      {t(
                        'game.content.koth.guide.refresh.cooldown',
                        'On refresh, the RECENT leader (top scorer in the just-ended window) is throttled at the WG sidecar for ONE tick — they can’t reach the hill while everyone else races to be first on the fresh box. Lifts automatically the tick after.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.koth.guide.refresh.recent_window',
                        'Leader is "recent leader" — winner of the last refresh window only, not cumulative-game leader. So a team that pulls ahead early doesn’t get throttled forever; the cooldown rotates among whoever is winning right now.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.koth.guide.refresh.timing_tip',
                        'Refresh fires at the START of the round AFTER the boundary (rounds 6, 11, 16, … for refreshTicks=5). So the round AT the boundary still gets scored before any wipe — your last hold credit lands.'
                      )}
                    </List.Item>
                  </List>
                </Accordion.Panel>
              </Accordion.Item>

              {/* DO / DON'T */}
              <Accordion.Item value="do_dont">
                <Accordion.Control
                  icon={<Icon path={mdiCounter} size={1} color="var(--mantine-color-gray-6)" />}
                >
                  <Text fw={600}>{t('game.content.koth.guide.do_dont.title', "Do / Don't")}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="xs">
                    <Text size="sm" fw={600} c="teal">
                      {t('game.content.koth.guide.do_dont.do', 'Do')}
                    </Text>
                    <List size="sm" spacing={2}>
                      <List.Item>
                        {t(
                          'game.content.koth.guide.do_dont.do_script',
                          'Script the token-fetch + plant loop. Manual planting means you’ll miss rounds — and a missed plant on a tick you would’ve held is 1 lost point.'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.koth.guide.do_dont.do_plant_late',
                          'Plant LATE in the tick. The last write before the checker peeks wins — racing to overwrite a rival is the whole game.'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.koth.guide.do_dont.do_repatch',
                          'Have a re-exploit + re-patch script ready for the 5-tick refresh — first team to re-establish a foothold after the wipe wins the next window.'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.koth.guide.do_dont.do_watch_status',
                          'Watch the hill status badge — if it’s Mumble/Offline you might be losing 1 pt per tick to the broken-hill penalty.'
                        )}
                      </List.Item>
                    </List>
                    <Text size="sm" fw={600} c="red" mt="xs">
                      {t('game.content.koth.guide.do_dont.dont', "Don't")}
                    </Text>
                    <List size="sm" spacing={2}>
                      <List.Item>
                        {t(
                          'game.content.koth.guide.do_dont.dont_stale_token',
                          'Plant yesterday’s token — it’s rotated, won’t match anything, attribution = none.'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.koth.guide.do_dont.dont_replay',
                          'Plant a token you observed in /koth/king from another team — it’ll credit THEM, not you. (You’re also flagged in the audit log.)'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.koth.guide.do_dont.dont_dos',
                          'DoS the hill so nobody scores — everyone gets −1 for the broken tick AND organizers see it in the traffic logs. Instant DQ.'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.koth.guide.do_dont.dont_persist',
                          'Try to persist tricks across the 5-tick wipe (cron jobs, systemd, host mounts) — the container is fully gone on each refresh; nothing carries over.'
                        )}
                      </List.Item>
                    </List>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>
            </Accordion>

            <Text size="xs" c="dimmed" ta="center">
              {t('game.content.koth.guide.footer', 'Endpoint base: ')}
              <Anchor href={apiUrl} target="_blank" rel="noreferrer" className={misc.ffmono}>
                {apiUrl}
              </Anchor>
            </Text>
          </Stack>
        </ScrollArea>
      </Modal>

      {/* Fresh-token reveal — shared with A&D (see AdToolkitSections). */}
      <AdTokenRevealModal
        opened={tokenModalOpen}
        onClose={closeTokenModal}
        freshToken={freshToken}
        title={t('game.content.koth.token_modal.title', 'Your new API token (KotH + A&D)')}
        warning={t(
          'game.content.koth.token_modal.warning',
          'This token is now saved in this browser (see “Saved token” in the API-token section) so your scripts can reuse it. Copy it here too if you want it elsewhere — the platform keeps only a hash and can’t show it again. The previous token (if any) has been invalidated. The same token authenticates both /Koth/{id}/Token and /Submit.'
        )}
      />
    </>
  )
}
