import {
  Badge,
  Center,
  Group,
  Loader,
  Paper,
  ScrollArea,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core'
import { mdiSwordCross, mdiTrophyOutline } from '@mdi/js'
import { Icon } from '@mdi/react'
import { FC } from 'react'
import { useTranslation } from 'react-i18next'
import { useAdScoreboard } from '@Hooks/useGame'

interface AdScoreboardTableProps {
  numId: number
  /** Highlight this participation's row (the viewer's own team). */
  myParticipationId?: number | null
}

export const AdScoreboardTable: FC<AdScoreboardTableProps> = ({ numId, myParticipationId }) => {
  const { t } = useTranslation()
  const { adScoreboard } = useAdScoreboard(numId)

  if (!adScoreboard) {
    return (
      <Center h="40vh">
        <Loader />
      </Center>
    )
  }

  if (adScoreboard.teams.length === 0 || adScoreboard.latestRound === 0) {
    return (
      <Paper p="xl" withBorder>
        <Stack align="center" gap="xs">
          <Icon path={mdiSwordCross} size={2.5} color="var(--mantine-color-dimmed)" />
          <Text fw="bold" c="dimmed">
            {t('game.content.scoreboard.ad.empty.title', 'No A&D rounds yet')}
          </Text>
          <Text size="sm" c="dimmed">
            {t('game.content.scoreboard.ad.empty.description',
              'The board will fill in once the first round completes.')}
          </Text>
        </Stack>
      </Paper>
    )
  }

  return (
    <Paper p="md" withBorder>
      <Group justify="space-between" mb="sm">
        <Group gap="xs">
          <Icon path={mdiTrophyOutline} size={1} />
          <Title order={4}>
            {t('game.content.scoreboard.ad.title', 'Attack & Defense scoreboard')}
          </Title>
        </Group>
        <Text size="xs" c="dimmed">
          {t('game.content.scoreboard.ad.latest_round', { round: adScoreboard.latestRound, defaultValue: 'through round {{round}}' })}
        </Text>
      </Group>
      <ScrollArea>
        <Table verticalSpacing="xs" striped highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>#</Table.Th>
              <Table.Th>{t('game.content.scoreboard.ad.column.team', 'Team')}</Table.Th>
              <Table.Th>{t('game.content.scoreboard.ad.column.division', 'Division')}</Table.Th>
              <Table.Th ta="right">{t('game.content.scoreboard.ad.column.total', 'Total')}</Table.Th>
              <Table.Th ta="right">{t('game.content.scoreboard.ad.column.attack', 'Attack')}</Table.Th>
              <Table.Th ta="right">{t('game.content.scoreboard.ad.column.defense_loss', 'Defense loss')}</Table.Th>
              <Table.Th ta="right">{t('game.content.scoreboard.ad.column.sla', 'SLA')}</Table.Th>
              <Table.Th ta="right">{t('game.content.scoreboard.ad.column.captures', 'Captures')}</Table.Th>
              <Table.Th ta="right">{t('game.content.scoreboard.ad.column.times_captured', 'Times captured')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {adScoreboard.teams.map((row) => {
              const isMine = myParticipationId === row.participationId
              return (
                <Table.Tr
                  key={row.participationId}
                  style={isMine ? { background: 'var(--mantine-color-brand-light)' } : undefined}
                >
                  <Table.Td>
                    {row.rank <= 3 ? (
                      <Badge color={['yellow', 'gray', 'orange'][row.rank - 1]} variant="filled">
                        {row.rank}
                      </Badge>
                    ) : (
                      <Text size="sm" c="dimmed">
                        {row.rank}
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Text fw={isMine ? 'bold' : undefined}>{row.teamName}</Text>
                  </Table.Td>
                  <Table.Td>
                    {row.division && (
                      <Badge size="xs" variant="light">
                        {row.division}
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td ta="right">
                    <Text fw="bold" ff="monospace">{row.total.toFixed(1)}</Text>
                  </Table.Td>
                  <Table.Td ta="right">
                    <Text size="sm" c="teal" ff="monospace">+{row.attackPoints.toFixed(1)}</Text>
                  </Table.Td>
                  <Table.Td ta="right">
                    <Text size="sm" c={row.defenseLoss > 0 ? 'red' : 'dimmed'} ff="monospace">
                      {row.defenseLoss > 0 ? `−${row.defenseLoss.toFixed(1)}` : '0.0'}
                    </Text>
                  </Table.Td>
                  <Table.Td ta="right">
                    <Text size="sm" c="blue" ff="monospace">{row.slaPoints.toFixed(1)}</Text>
                  </Table.Td>
                  <Table.Td ta="right">
                    <Text size="sm" ff="monospace">{row.flagsCaptured}</Text>
                  </Table.Td>
                  <Table.Td ta="right">
                    <Text size="sm" c={row.timesCaptured > 0 ? 'red' : 'dimmed'} ff="monospace">
                      {row.timesCaptured}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              )
            })}
          </Table.Tbody>
        </Table>
      </ScrollArea>
    </Paper>
  )
}
